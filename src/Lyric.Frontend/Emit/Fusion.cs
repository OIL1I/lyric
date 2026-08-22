using Lyric.Core;
using Lyric.Ir;

namespace Lyric.Bytecode;

/// <summary>One instruction that stands for several IR operations.</summary>
/// <param name="Opcode">The fused opcode itself.</param>
/// <param name="Kind">What it computes: a comparison for the branches, a binary operator for the
/// arithmetic forms. The same <see cref="Op"/> value the unfused instruction would have carried,
/// which is why the fused forms need no enumeration of their own.</param>
/// <param name="Type">The tag of the OPERANDS, as on the unfused comparison: <c>i64</c> and
/// <c>u64</c> are different machine operations and the result type says nothing about which.</param>
/// <param name="Constant">The right-hand operand of a constant shape; <c>null</c> for the
/// slot-and-slot shapes.</param>
/// <param name="Consumed">How many entries of the block this instruction replaces. The
/// terminator, when one is consumed, is not counted here — <see cref="FusionPlan.EndsBlock"/>
/// says so instead.</param>
internal readonly record struct FusedInstruction(
    Op Opcode,
    Op Kind,
    TypeTag Type,
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
/// executing fewer instructions. Five of the nine instructions in a bare counting loop are
/// bookkeeping: load the counter, load the bound, compare, branch.</para>
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
        if (block.Terminator is not CondBranch branch) return FusionPlan.None;

        var insts = block.Insts;
        if (insts.Count < 3) return FusionPlan.None;

        // The comparison has to be the last thing the block computes: anything after it would
        // run between the comparison and the branch, and the fused instruction has no room for it.
        if (insts[^1] is not BinOp comparison) return FusionPlan.None;
        if (comparison.Dest != branch.Cond) return FusionPlan.None;
        if (ComparisonOpcode(comparison.Kind) is not { } kind) return FusionPlan.None;

        var uses = UseCounts(block);
        if (!Swallowable(comparison.Dest, layout, uses)) return FusionPlan.None;

        // The operand tag, read from the temp table for the same reason the unfused emitter reads
        // it there: a comparison's own type is bool.
        var tag = TagOf(function.Temps[comparison.Lhs.Value].Type);
        if (!Fusible(tag)) return FusionPlan.None;

        if (insts[^3] is not LoadLocal left) return FusionPlan.None;
        if (left.Dest != comparison.Lhs) return FusionPlan.None;
        if (!Swallowable(left.Dest, layout, uses)) return FusionPlan.None;

        var fused = insts[^2] switch
        {
            LoadLocal right when right.Dest == comparison.Rhs
                                 && Swallowable(right.Dest, layout, uses) =>
                new FusedInstruction(Op.BranchCompare, kind, tag,
                    left.Local.Value, right.Local.Value, null,
                    branch.IfTrue.Value, branch.IfFalse.Value, Consumed: 3),

            Const right when right.Dest == comparison.Rhs
                             && Swallowable(right.Dest, layout, uses) =>
                new FusedInstruction(Op.BranchCompareConst, kind, tag,
                    left.Local.Value, -1, right.Value,
                    branch.IfTrue.Value, branch.IfFalse.Value, Consumed: 3),

            _ => default,
        };

        if (fused.Consumed == 0) return FusionPlan.None;

        return new FusionPlan(
            new Dictionary<int, FusedInstruction> { [insts.Count - 3] = fused },
            EndsBlock: true);
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
    /// comparison over a word; a string or a reference is not, and a tag that arrives here without
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
