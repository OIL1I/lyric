using Lyric.Core;

namespace Lyric.Ir;

/// <summary>
/// Replaces a direct call to a small function with the function's body.
///
/// <para>The frame is the cost this removes: after pooling a call still pays argument copying,
/// dispatch and two array writes per slot, which for a three-line method is more than the method.
/// Inlining is also what makes scalar replacement possible at all — a value that is returned
/// escapes its own function, but not the caller it was inlined into.</para>
///
/// <para>ON THE IR, LIKE THE REACHABILITY ANALYSIS, and cheap for the same reason: dispatch is
/// static, generics are monomorphized, and values cross block boundaries through locals rather
/// than phis — splicing foreign blocks is renumbering plus one branch in, one branch out.</para>
///
/// <para>WHAT IS DELIBERATELY NOT INLINED:</para>
/// <list type="bullet">
/// <item>A caller with handlers. Handler ranges are contiguous block ranges, and spliced blocks
/// land at the end of the block list — outside every range. A throw from the spliced body would
/// sail past the very catch that used to receive it. Skipping such callers keeps ranges honest;
/// hot loops do not carry try blocks.</item>
/// <item>A callee with handlers or an <c>endfinally</c>, for the mirrored reason: its ranges
/// cannot come along.</item>
/// <item>A self call. Mutual recursion needs no analysis beyond this: splicing a cycle
/// eventually surfaces the call to the function itself, which is refused, and the size budget
/// bounds everything before that.</item>
/// <item>Dynamic calls — <c>callvirt</c>, <c>callind</c>, imports. Their target is not in the
/// instruction.</item>
/// </list>
///
/// <para>The spliced instructions KEEP THE CALLEE'S SPANS: a panic then names the right line in
/// the right file, but the frame it names is the caller's — the same trade every optimizing
/// compiler makes, and the cheaper half of inline debug info.</para>
///
/// <para>The pass relies on definite assignment: a callee local is written before it is read, so
/// reusing its caller slot across loop iterations cannot expose a stale value. The sema
/// guarantees that for written code; the lowering's synthetic locals hold it by construction.</para>
/// </summary>
internal static class Inliner
{
    /// <summary>Callee budget in instructions. Sized to admit the shapes the measurements name —
    /// a field-wise constructor method (<c>Vec2.add</c>, ~12), an iterator's <c>next</c> (~15) —
    /// and to refuse anything that would trade code size for nothing.</summary>
    private const int MaxCalleeOps = 24;

    /// <summary>Growth cap per caller, counted in spliced sites. A pathological fan-out stops
    /// here rather than in the emitter.</summary>
    private const int MaxSitesPerFunction = 64;

    public static void Run(IrModule module)
    {
        // Two rounds: scanning already continues through freshly spliced blocks, so a chain
        // A -> B -> C collapses in one pass. The second round only catches a callee that itself
        // became smaller-than-budget knowledge too late — and usually changes nothing.
        for (var round = 0; round < 2; round++)
        {
            var changed = false;
            foreach (var function in module.Functions)
                changed |= InlineInto(function, module);
            if (!changed) break;
        }
    }

    private static bool InlineInto(IrFunction caller, IrModule module)
    {
        if (caller.Handlers.Count > 0) return false;

        var sites = 0;
        var changed = false;

        for (var bi = 0; bi < caller.Blocks.Count; bi++)
        {
            var block = caller.Blocks[bi];
            for (var ii = 0; ii < block.Insts.Count; ii++)
            {
                if (sites >= MaxSitesPerFunction) return changed;
                if (block.Insts[ii] is not Call call) continue;

                var callee = module.Functions[call.Target.Value];
                if (ReferenceEquals(callee, caller) || !Inlinable(callee)) continue;

                Splice(caller, block, ii, call, callee);
                sites++;
                changed = true;
                // The block was cut at the call; its tail lives in the continuation block at the
                // end of the list, which this loop reaches later — spliced blocks included, so a
                // chain of small calls collapses in one pass.
                break;
            }
        }

        return changed;
    }

    private static bool Inlinable(IrFunction callee)
    {
        if (callee.Handlers.Count > 0) return false;

        var ops = 0;
        var returns = false;
        foreach (var block in callee.Blocks)
        {
            ops += block.Insts.Count;
            if (block.Terminator is EndFinally) return false;
            if (block.Terminator is Return) returns = true;
        }

        // A callee that never returns — every path throws — would leave the continuation block
        // without a predecessor, and everything dominated by the call would hang off it. The
        // verifier rejects exactly that as unreachable, so such a callee stays a call.
        return returns && ops <= MaxCalleeOps;
    }

    /// <summary>
    /// Replaces <paramref name="call"/> — instruction <paramref name="at"/> of
    /// <paramref name="block"/> — with the body of <paramref name="callee"/>.
    ///
    /// <para>The callee's locals, temps and blocks are appended to the caller's tables with their
    /// ids shifted, which keeps every table dense. The block is cut at the call: its head stores
    /// the arguments into the callee's parameter locals — the same convention a frame follows —
    /// and branches into the copy; every <c>return</c> becomes a branch to a continuation block
    /// holding the tail.</para>
    ///
    /// <para>The result travels through a synthetic local rather than into the call's dest temp
    /// directly: a temp is defined exactly once, and a callee may return from several blocks.
    /// The continuation loads the local into the dest — one store and one load, which scalar
    /// replacement's forwarding does not remove today; measured against the frame it replaces,
    /// the pair is noise.</para>
    /// </summary>
    private static void Splice(IrFunction caller, IrBlock block, int at, Call call,
        IrFunction callee)
    {
        if (call.Args.Length != callee.ParamCount)
            throw new InternalCompilationException(
                $"ir: call to '{callee.Name}' carries {call.Args.Length} argument(s), " +
                $"the function declares {callee.ParamCount}");

        var localBase = caller.Locals.Count;
        foreach (var source in callee.Locals)
            caller.Locals.Add(new IrLocal(new LocalId(caller.Locals.Count),
                $"__inl_{source.Name}", source.Type));

        var tempBase = caller.Temps.Count;
        foreach (var source in callee.Temps)
            caller.Temps.Add(new IrTemp(new TempId(caller.Temps.Count), source.Type));

        LocalId? resultLocal = null;
        if (call.Dest is not null)
        {
            resultLocal = new LocalId(caller.Locals.Count);
            caller.Locals.Add(new IrLocal(resultLocal.Value, "__inl_ret", callee.ReturnType));
        }

        var blockBase = caller.Blocks.Count;
        var continuationId = new BlockId(blockBase + callee.Blocks.Count);

        TempId MapTemp(TempId id) => new(tempBase + id.Value);
        LocalId MapLocal(LocalId id) => new(localBase + id.Value);
        BlockId MapBlock(BlockId id) => new(blockBase + id.Value);

        foreach (var source in callee.Blocks)
        {
            var insts = new List<IrOp>(source.Insts.Count);
            foreach (var op in source.Insts) insts.Add(IrShape.Rewrite(op, MapTemp, MapLocal));

            var copy = new IrBlock(MapBlock(source.Id), insts);
            switch (source.Terminator)
            {
                case Return { Value: { } value } r when resultLocal is { } result:
                    insts.Add(new StoreLocal(result, MapTemp(value), r.Span));
                    copy.Terminator = new Branch(continuationId, r.Span);
                    break;

                // A discarded result (dest is null) is dropped here, exactly as the call
                // dropped it; a void return carries nothing.
                case Return r:
                    copy.Terminator = new Branch(continuationId, r.Span);
                    break;

                case { } terminator:
                    copy.Terminator = IrShape.Rewrite(terminator, MapTemp, MapBlock);
                    break;

                default:
                    throw new InternalCompilationException(
                        $"ir: block {source.Id} of '{callee.Name}' has no terminator");
            }

            caller.Blocks.Add(copy);
        }

        var continuation = new IrBlock(continuationId, new List<IrOp>());
        if (call.Dest is { } dest)
            continuation.Insts.Add(new LoadLocal(dest, resultLocal!.Value, callee.ReturnType,
                call.Span));
        for (var i = at + 1; i < block.Insts.Count; i++) continuation.Insts.Add(block.Insts[i]);
        continuation.Terminator = block.Terminator;
        caller.Blocks.Add(continuation);

        // The cut head: feed the parameters, enter the copy. Argument order is parameter order,
        // the same convention the frame construction followed.
        block.Insts.RemoveRange(at, block.Insts.Count - at);
        for (var i = 0; i < call.Args.Length; i++)
            block.Insts.Add(new StoreLocal(new LocalId(localBase + i), call.Args[i], call.Span));
        block.Terminator = new Branch(new BlockId(blockBase + callee.Entry.Value), call.Span);
    }
}
