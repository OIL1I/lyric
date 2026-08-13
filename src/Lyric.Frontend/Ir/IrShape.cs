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
}
