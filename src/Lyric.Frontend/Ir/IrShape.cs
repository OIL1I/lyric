using Lyric.Core;

namespace Lyric.Ir;

/// <summary>
/// Structural access to instructions: which temps does one read, which does it write, where does it
/// branch.
///
/// <para>A class of its own, because several stages ask the same question — the verifier for def/use
/// and reachability, the bytecode emitter for stack scheduling. Two copies of these <c>switch</c>
/// blocks would be a drift risk of the worst kind: a new instruction missing from one copy leads to
/// silently wrong code rather than to an error.</para>
///
/// <para>The <c>default</c> throw is the completeness guarantee here too: a new instruction breaks
/// immediately and visibly rather than silently passing as operand-free.</para>
/// </summary>
public static class IrShape
{
    public static IReadOnlyList<TempId> OperandsOf(IrOp op) => op switch
    {
        Const => Array.Empty<TempId>(),
        BinOp b => new[] { b.Lhs, b.Rhs },
        UnOp u => new[] { u.Operand },
        Convert cv => new[] { cv.Operand },
        LoadLocal => Array.Empty<TempId>(),
        StoreLocal s => new[] { s.Value },
        Call k => k.Args,
        CallImport k => k.Args,
        NewObject => Array.Empty<TempId>(),
        LoadField f => new[] { f.Object },
        // The order is a contract, not taste: the stack scheduler places the operands in exactly this
        // sequence, and the format fixes that for stfld the reference lies below the value. Swapping
        // here means swapped arguments in the VM.
        StoreField f => new[] { f.Object, f.Value },

        NewArray a => a.Elements,
        LoadElem e => new[] { e.Array, e.Index },
        // The order is a contract: array, index, value, from the bottom up.
        StoreElem e => new[] { e.Array, e.Index, e.Value },
        ArrayLen a => new[] { a.Array },
        ArrayConcat c => new[] { c.Left, c.Right },
        ArrayRepeat r => new[] { r.Array, r.Count },

        OptNone => Array.Empty<TempId>(),
        OptSome s => new[] { s.Value },
        OptIsSome i => new[] { i.Option },
        OptGet g => new[] { g.Option },

        NewVariant v => v.Fields,
        EnumTag t => new[] { t.Value },
        EnumAs a => new[] { a.Value },

        MakeInterface m => new[] { m.Value },
        StructCopy c => new[] { c.Value },

        LoadGlobal => Array.Empty<TempId>(),
        StoreGlobal g => new[] { g.Value },

        // The environment is the only operand; the function index stands in the instruction.
        MakeClosure m => m.Environment is { } env ? new[] { env } : Array.Empty<TempId>(),
        // The callee lies BEFORE the arguments, like the receiver at a callvirt.
        CallIndirect c => new[] { c.Callee }.Concat(c.Args).ToArray(),
        // The receiver is argument 0 and therefore lies lowest, the same convention as at Call.
        // CallVirt needs no special handling.
        CallVirt c => c.Args,

        _ => throw new InternalCompilationException($"ir: unhandled op {op.GetType().Name}")
    };

    public static IReadOnlyList<TempId> OperandsOf(IrTerminator terminator) => terminator switch
    {
        Return r => r.Value is { } value ? new[] { value } : Array.Empty<TempId>(),
        Branch => Array.Empty<TempId>(),
        CondBranch c => new[] { c.Cond },
        Unreachable => Array.Empty<TempId>(),
        Throw t => new[] { t.Value },
        EndFinally => Array.Empty<TempId>(),
        _ => throw new InternalCompilationException(
            $"ir: unhandled terminator {terminator.GetType().Name}")
    };

    /// <summary>The temp the instruction defines, or <c>null</c> when it writes none (<c>store</c>, a
    /// void <c>call</c>).</summary>
    public static TempId? DestOf(IrOp op) => op switch
    {
        Const c => c.Dest,
        BinOp b => b.Dest,
        UnOp u => u.Dest,
        Convert cv => cv.Dest,
        LoadLocal l => l.Dest,
        StoreLocal => null,
        Call k => k.Dest,
        CallImport k => k.Dest,
        NewObject n => n.Dest,
        LoadField f => f.Dest,
        StoreField => null,

        NewArray a => a.Dest,
        LoadElem e => e.Dest,
        StoreElem => null,
        ArrayLen a => a.Dest,
        ArrayConcat c => c.Dest,
        ArrayRepeat r => r.Dest,

        OptNone n => n.Dest,
        OptSome s => s.Dest,
        OptIsSome i => i.Dest,
        OptGet g => g.Dest,

        NewVariant v => v.Dest,
        EnumTag t => t.Dest,
        EnumAs a => a.Dest,

        MakeInterface m => m.Dest,
        StructCopy c => c.Dest,

        LoadGlobal l => l.Dest,
        StoreGlobal => null,
        MakeClosure m => m.Dest,
        CallIndirect c => c.Dest,
        CallVirt c => c.Dest,

        _ => throw new InternalCompilationException($"ir: unhandled op {op.GetType().Name}")
    };

    public static IReadOnlyList<BlockId> SuccessorsOf(IrTerminator terminator) => terminator switch
    {
        Return => Array.Empty<BlockId>(),
        Branch b => new[] { b.Target },
        CondBranch c => new[] { c.IfTrue, c.IfFalse },
        // Throw and EndFinally have no successors IN THE CFG: where execution continues is decided by
        // the handler table, not by the block's control flow. The verifier therefore treats handler
        // blocks separately as reachable.
        Unreachable or Throw or EndFinally => Array.Empty<BlockId>(),
        _ => throw new InternalCompilationException(
            $"ir: unhandled terminator {terminator.GetType().Name}")
    };

    // ------------------------------------------------------------------ rewriting
    //
    // The id maps live here for the same reason the operand lists do: an instruction missing from
    // a copy of this switch would keep its old ids and produce silently wrong code. The inliner is
    // the consumer; function, type, import and global ids are module-wide and stay untouched.

    /// <summary>The instruction with every temp and local id passed through the two maps.</summary>
    public static IrOp Rewrite(IrOp op, Func<TempId, TempId> temp, Func<LocalId, LocalId> local)
    {
        TempId? Opt(TempId? id) => id is { } value ? temp(value) : null;
        TempId[] All(TempId[] ids)
        {
            var mapped = new TempId[ids.Length];
            for (var i = 0; i < ids.Length; i++) mapped[i] = temp(ids[i]);
            return mapped;
        }

        return op switch
        {
            Const c => c with { Dest = temp(c.Dest) },
            BinOp b => b with { Dest = temp(b.Dest), Lhs = temp(b.Lhs), Rhs = temp(b.Rhs) },
            UnOp u => u with { Dest = temp(u.Dest), Operand = temp(u.Operand) },
            Convert cv => cv with { Dest = temp(cv.Dest), Operand = temp(cv.Operand) },
            LoadLocal l => l with { Dest = temp(l.Dest), Local = local(l.Local) },
            StoreLocal s => s with { Local = local(s.Local), Value = temp(s.Value) },
            Call k => k with { Dest = Opt(k.Dest), Args = All(k.Args) },
            CallImport k => k with { Dest = Opt(k.Dest), Args = All(k.Args) },
            NewObject n => n with { Dest = temp(n.Dest) },
            LoadField f => f with { Dest = temp(f.Dest), Object = temp(f.Object) },
            StoreField f => f with { Object = temp(f.Object), Value = temp(f.Value) },

            NewArray a => a with { Dest = temp(a.Dest), Elements = All(a.Elements) },
            LoadElem e => e with { Dest = temp(e.Dest), Array = temp(e.Array), Index = temp(e.Index) },
            StoreElem e => e with { Array = temp(e.Array), Index = temp(e.Index), Value = temp(e.Value) },
            ArrayLen a => a with { Dest = temp(a.Dest), Array = temp(a.Array) },
            ArrayConcat c => c with { Dest = temp(c.Dest), Left = temp(c.Left), Right = temp(c.Right) },
            ArrayRepeat r => r with { Dest = temp(r.Dest), Array = temp(r.Array), Count = temp(r.Count) },

            OptNone n => n with { Dest = temp(n.Dest) },
            OptSome s => s with { Dest = temp(s.Dest), Value = temp(s.Value) },
            OptIsSome i => i with { Dest = temp(i.Dest), Option = temp(i.Option) },
            OptGet g => g with { Dest = temp(g.Dest), Option = temp(g.Option) },

            NewVariant v => v with { Dest = temp(v.Dest), Fields = All(v.Fields) },
            EnumTag t => t with { Dest = temp(t.Dest), Value = temp(t.Value) },
            EnumAs a => a with { Dest = temp(a.Dest), Value = temp(a.Value) },

            MakeInterface m => m with { Dest = temp(m.Dest), Value = temp(m.Value) },
            StructCopy c => c with { Dest = temp(c.Dest), Value = temp(c.Value) },

            LoadGlobal l => l with { Dest = temp(l.Dest) },
            StoreGlobal g => g with { Value = temp(g.Value) },

            MakeClosure m => m with { Dest = temp(m.Dest), Environment = Opt(m.Environment) },
            CallIndirect c => c with
            {
                Dest = Opt(c.Dest), Callee = temp(c.Callee), Args = All(c.Args),
            },
            CallVirt c => c with { Dest = Opt(c.Dest), Args = All(c.Args) },

            _ => throw new InternalCompilationException($"ir: unhandled op {op.GetType().Name}")
        };
    }

    /// <summary>The terminator with its temp and block ids passed through the two maps.</summary>
    public static IrTerminator Rewrite(IrTerminator terminator, Func<TempId, TempId> temp,
        Func<BlockId, BlockId> block) => terminator switch
    {
        Return r => r with { Value = r.Value is { } value ? temp(value) : null },
        Branch b => b with { Target = block(b.Target) },
        CondBranch c => c with
        {
            Cond = temp(c.Cond), IfTrue = block(c.IfTrue), IfFalse = block(c.IfFalse),
        },
        Throw t => t with { Value = temp(t.Value) },
        Unreachable or EndFinally => terminator,
        _ => throw new InternalCompilationException(
            $"ir: unhandled terminator {terminator.GetType().Name}")
    };
}
