using Lyric.Core;
using Lyric.Ir;

namespace Lyric.Bytecode;

/// <summary>One instruction that stands for several IR operations.</summary>
/// <param name="Opcode">The fused opcode itself.</param>
/// <param name="Kind">What it computes: a comparison for the branches, any binary operation for
/// the arithmetic forms. The same <see cref="Op"/> value the unfused instruction would have
/// carried, which is why the fused forms need no enumeration of their own.</param>
/// <param name="Type">The tag of the OPERANDS, as on the unfused operation: <c>i64</c> and
/// <c>u64</c> are different machine operations, and for a comparison the result type says nothing
/// about which.</param>
/// <param name="SlotDest">Where the result goes; -1 for the branches, which produce none.</param>
/// <param name="Constant">The right-hand operand of a constant shape; <c>null</c> for the
/// slot-and-slot shapes.</param>
/// <param name="Consumed">How many entries of the block this instruction replaces. A consumed
/// terminator is not counted here — <see cref="FusionPlan.EndsBlock"/> says so instead.</param>
internal readonly record struct FusedInstruction(
    Op Opcode,
    Op Kind,
    TypeTag Type,
    int SlotDest,
    int SlotA,
    int SlotB,
    IrConstValue? Constant,
    int IfTrue,
    int IfFalse,
    int Consumed);

/// <summary>What the emitter should do differently in one block.</summary>
/// <param name="At">Keyed by the index of the FIRST instruction each fusion replaces.</param>
/// <param name="EndsBlock">Whether the last fusion also stands for the block's terminator.</param>
internal sealed record FusionPlan(IReadOnlyDictionary<int, FusedInstruction> At, bool EndsBlock)
{
    public static readonly FusionPlan None =
        new(new Dictionary<int, FusedInstruction>(), EndsBlock: false);
}

/// <summary>
/// Instruction selection: which runs of IR operations become one bytecode instruction.
///
/// <para><b>Why here and not in the IR.</b> A fused instruction is a property of the ENCODING, not
/// of the program. The IR is the machine-independent form every pass reads — the verifier, the
/// printer, the inliner, scalar replacement — and teaching all of them a backend shape to save the
/// emitter a step would be the wrong trade twice over. This is the same place a compiler with a
/// real backend does instruction selection, and for the same reason.</para>
///
/// <para><b>Why it is worth doing at all.</b> Measured on this interpreter: an instruction costs
/// ~6 ns and costs it regardless of what it does — a <c>br</c> and an <c>add f64</c> are within
/// twenty percent. The dispatch is the whole bill, so the only thing that moves the number is
/// executing fewer instructions. Of the nine instructions in a bare counting loop, four are the
/// loop test and four more are the counter's load-operate-store traffic.</para>
///
/// <para><b>The two shapes.</b> A comparison whose only reader is the block's branch, and a binary
/// operation whose operands are slots and whose result goes straight into one. Between them they
/// cover the loop test and the accumulator, which is most of what a loop body IS.</para>
///
/// <para><b>The rules a match has to satisfy</b>, all of them about not changing what runs:</para>
/// <list type="bullet">
/// <item>The operations are ADJACENT and in evaluation order. A gap would mean something happened
/// between them, and whatever it was would have to move.</item>
/// <item>Every temp the fusion swallows is used EXACTLY ONCE, inside the fusion. A second reader
/// would find a value nobody computed any more.</item>
/// <item>Every such temp lives on the operand stack rather than in a slot. A slot-placed temp is
/// written and read by instructions this does not see, and reasoning about them buys nothing —
/// the pattern occurs on the stack.</item>
/// <item>The constant shape takes its constant on the RIGHT. <c>x - 1</c> and <c>1 - x</c> are
/// different, and there is no shape for the second.</item>
/// </list>
///
/// <para>Every rule fails SAFELY: no match means the unfused instructions are emitted, which is
/// what this compiler did before. Correctness never depends on a fusion happening.</para>
/// </summary>
internal static class Fusion
{
    /// <summary>The plan for one block, or <see cref="FusionPlan.None"/> when nothing matches.
    /// </summary>
    public static FusionPlan Of(IrFunction function, IrBlock block, FunctionLayout layout)
    {
        var uses = UseCounts(block);
        var at = new Dictionary<int, FusedInstruction>();

        // Left to right, skipping past what a match swallows, so no two fusions can claim the
        // same operation.
        for (var i = 0; i < block.Insts.Count; i++)
        {
            if (BinaryAt(function, block, layout, uses, i) is not { } binary) continue;
            at[i] = binary;
            i += binary.Consumed - 1;
        }

        // The tail branch comes last because it swallows the TERMINATOR rather than an operation,
        // and because it is the only fusion that can end a block.
        if (BranchAt(function, block, layout, uses) is not { } branch)
            return at.Count == 0 ? FusionPlan.None : new FusionPlan(at, EndsBlock: false);

        // An arithmetic fusion reaching into the branch's operations wins: it was found first, and
        // taking its operations away now would emit an instruction reading a value nobody
        // computed. In practice the two do not overlap — a block ending in a comparison does not
        // end in a store — and the guard is here because "in practice" is not a reason.
        var start = block.Insts.Count - branch.Consumed;
        if (at.Any(entry => entry.Key + entry.Value.Consumed > start))
            return new FusionPlan(at, EndsBlock: false);

        at[start] = branch;
        return new FusionPlan(at, EndsBlock: true);
    }

    /// <summary>
    /// <c>dest = a op b</c> or <c>dest = a op k</c>: two loads, the operation, the store.
    /// </summary>
    private static FusedInstruction? BinaryAt(IrFunction function, IrBlock block,
        FunctionLayout layout, Dictionary<TempId, int> uses, int i)
    {
        if (i + 3 >= block.Insts.Count) return null;
        if (block.Insts[i] is not LoadLocal left) return null;
        if (block.Insts[i + 2] is not BinOp operation) return null;
        if (block.Insts[i + 3] is not StoreLocal store) return null;

        if (operation.Lhs != left.Dest || store.Value != operation.Dest) return null;
        if (!FusibleBinary(operation.Kind)) return null;
        if (!Swallowable(left.Dest, layout, uses)) return null;
        if (!Swallowable(operation.Dest, layout, uses)) return null;

        var tag = TagOf(function.Temps[operation.Lhs.Value].Type);
        if (!Fusible(tag)) return null;

        var kind = BinaryOpcode(operation.Kind);

        return block.Insts[i + 1] switch
        {
            LoadLocal right when right.Dest == operation.Rhs
                                 && Swallowable(right.Dest, layout, uses) =>
                new FusedInstruction(Op.BinLocals, kind, tag, store.Local.Value,
                    left.Local.Value, right.Local.Value, null, -1, -1, Consumed: 4),

            Const right when right.Dest == operation.Rhs
                             && Swallowable(right.Dest, layout, uses) =>
                new FusedInstruction(Op.BinConst, kind, tag, store.Local.Value,
                    left.Local.Value, -1, right.Value, -1, -1, Consumed: 4),

            _ => null,
        };
    }

    /// <summary>A comparison at the end of a block whose only reader is the branch below it.
    /// </summary>
    private static FusedInstruction? BranchAt(IrFunction function, IrBlock block,
        FunctionLayout layout, Dictionary<TempId, int> uses)
    {
        if (block.Terminator is not CondBranch branch) return null;

        var insts = block.Insts;
        if (insts.Count < 3) return null;

        // The comparison has to be the last thing the block computes: anything after it would run
        // between the comparison and the branch, and the fused instruction has no room for it.
        if (insts[^1] is not BinOp comparison) return null;
        if (comparison.Dest != branch.Cond) return null;
        if (ComparisonOpcode(comparison.Kind) is not { } kind) return null;
        if (!Swallowable(comparison.Dest, layout, uses)) return null;

        // The operand tag, read from the temp table for the same reason the unfused emitter reads
        // it there: a comparison's own type is bool.
        var tag = TagOf(function.Temps[comparison.Lhs.Value].Type);
        if (!Fusible(tag)) return null;

        if (insts[^3] is not LoadLocal left) return null;
        if (left.Dest != comparison.Lhs) return null;
        if (!Swallowable(left.Dest, layout, uses)) return null;

        return insts[^2] switch
        {
            LoadLocal right when right.Dest == comparison.Rhs
                                 && Swallowable(right.Dest, layout, uses) =>
                new FusedInstruction(Op.BranchCompare, kind, tag, -1,
                    left.Local.Value, right.Local.Value, null,
                    branch.IfTrue.Value, branch.IfFalse.Value, Consumed: 3),

            Const right when right.Dest == comparison.Rhs
                             && Swallowable(right.Dest, layout, uses) =>
                new FusedInstruction(Op.BranchCompareConst, kind, tag, -1,
                    left.Local.Value, -1, right.Value,
                    branch.IfTrue.Value, branch.IfFalse.Value, Consumed: 3),

            _ => null,
        };
    }

    /// <summary>
    /// How often each temp is READ in this block, terminator included.
    ///
    /// <para>Per block rather than per function, and that is exact rather than approximate: a
    /// value that crosses a block boundary travels through a local, never through a temp — the
    /// invariant the whole lowering is built on and the reason this IR needs no phi.</para>
    /// </summary>
    private static Dictionary<TempId, int> UseCounts(IrBlock block)
    {
        var counts = new Dictionary<TempId, int>();
        foreach (var op in block.Insts)
            foreach (var operand in IrShape.OperandsOf(op))
                counts[operand] = counts.GetValueOrDefault(operand) + 1;

        if (block.Terminator is { } terminator)
            foreach (var operand in IrShape.OperandsOf(terminator))
                counts[operand] = counts.GetValueOrDefault(operand) + 1;

        return counts;
    }

    /// <summary>May the fusion take this temp with it — read once, and nowhere but on the stack?
    /// </summary>
    private static bool Swallowable(TempId temp, FunctionLayout layout,
        Dictionary<TempId, int> uses) =>
        uses.GetValueOrDefault(temp) == 1
        && layout.Placements.TryGetValue(temp, out var placement)
        && placement == Placement.Stack;

    /// <summary>
    /// May this operation stand in a fused form?
    ///
    /// <para>A list rather than "everything binary", although today it happens to be everything: a
    /// kind added later has to be decided about, not swept in by a default.</para>
    /// </summary>
    private static bool FusibleBinary(IrBinKind kind) => kind
        is IrBinKind.Add or IrBinKind.Sub or IrBinKind.Mul or IrBinKind.Div or IrBinKind.Rem
        or IrBinKind.Shl or IrBinKind.Shr
        or IrBinKind.BitAnd or IrBinKind.BitOr or IrBinKind.BitXor
        or IrBinKind.Lt or IrBinKind.Le or IrBinKind.Gt or IrBinKind.Ge
        or IrBinKind.Eq or IrBinKind.Ne;

    /// <summary>The opcode an <see cref="IrBinKind"/> stands for. Total over what
    /// <see cref="FusibleBinary"/> admits and throwing beyond it: a kind that arrived here
    /// unlisted would otherwise be emitted as whatever a default said.</summary>
    private static Op BinaryOpcode(IrBinKind kind) => kind switch
    {
        IrBinKind.Add => Op.Add,
        IrBinKind.Sub => Op.Sub,
        IrBinKind.Mul => Op.Mul,
        IrBinKind.Div => Op.Div,
        IrBinKind.Rem => Op.Rem,
        IrBinKind.Shl => Op.Shl,
        IrBinKind.Shr => Op.Shr,
        IrBinKind.BitAnd => Op.BitAnd,
        IrBinKind.BitOr => Op.BitOr,
        IrBinKind.BitXor => Op.BitXor,
        _ => ComparisonOpcode(kind)
             ?? throw new InternalCompilationException($"fusion: unknown binop {kind}"),
    };

    /// <summary>The comparison an <see cref="IrBinKind"/> stands for, or <c>null</c> when it is
    /// not one.</summary>
    private static Op? ComparisonOpcode(IrBinKind kind) => kind switch
    {
        IrBinKind.Lt => Op.Lt,
        IrBinKind.Le => Op.Le,
        IrBinKind.Gt => Op.Gt,
        IrBinKind.Ge => Op.Ge,
        IrBinKind.Eq => Op.Eq,
        IrBinKind.Ne => Op.Ne,
        _ => null,
    };

    /// <summary>
    /// Operand types a fused form takes: the scalars, and nothing else.
    ///
    /// <para>A whitelist rather than a list of exclusions. Everything here is one machine
    /// operation over a word; a string or a reference is not, and a tag that arrives here without
    /// being one would be executed as if it were.</para>
    /// </summary>
    private static bool Fusible(TypeTag tag) => tag
        is TypeTag.I8 or TypeTag.I16 or TypeTag.I32 or TypeTag.I64
        or TypeTag.U8 or TypeTag.U16 or TypeTag.U32 or TypeTag.U64
        or TypeTag.F32 or TypeTag.F64
        or TypeTag.Bool or TypeTag.Char;

    /// <summary>The tag of a scalar IR type; anything else is not fusible and says so by
    /// answering <see cref="TypeTag.Void"/>, which <see cref="Fusible"/> refuses. The writer's own
    /// <c>TagOf</c> is total and throws on what it does not know — right there, wrong here, where
    /// "not a scalar" is an ordinary answer.</summary>
    private static TypeTag TagOf(IrType type) =>
        type is IrScalarType scalar ? BytecodeWriter.TagOf(scalar) : TypeTag.Void;
}
