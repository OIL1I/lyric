namespace Lyric.Ir.Lowering;

/// <summary>
/// Where the <c>break</c> and <c>continue</c> of the innermost loop jump to.
///
/// <para>THE TARGETS ARISE ON DEMAND. Created eagerly, they turn <c>do { return … } while (…)</c> into
/// a compiler crash: if the body terminates, nobody reaches the condition and the exit, and the
/// verifier rejects unreachable blocks, as there is no <c>SimplifyCfg</c> pass. A block nobody enters
/// is now never created.</para>
///
/// <para>The fall-through flag cannot decide this: <c>do { if (c) { break; } return 1; }</c> does not
/// fall through and reaches the exit all the same. "Is the block reachable" and "does the body fall
/// through" are two different questions, and only the first counts here. This type therefore
/// remembers whether anyone actually requested the target.</para>
/// </summary>
internal sealed class LoopScope(BlockBuilder blocks)
{
    private BlockId? _continue;
    private BlockId? _break;

    /// <summary>The defer-stack depth OUTSIDE this loop. A <c>break</c> or <c>continue</c> leaves
    /// every scope above it and runs their defers first (§7.5) — without the mark it would either
    /// skip them or drain the scopes it does not leave.</summary>
    public int DeferDepth { get; init; }

    /// <summary>
    /// For <c>while</c> and <c>for-in</c>, where both blocks are ALWAYS reachable: the condition through
    /// the entry edge, the exit through its false edge. They also have to exist beforehand, because the
    /// <c>CondBranch</c> names them before the body is lowered.
    ///
    /// <para>Only <c>do-while</c> has the problem: there the condition stands BEHIND the body, and if
    /// that terminates, nobody reaches it.</para>
    /// </summary>
    public LoopScope(BlockBuilder blocks, BlockId continueTarget, BlockId breakTarget)
        : this(blocks)
    {
        _continue = continueTarget;
        _break = breakTarget;
    }

    /// <summary>The target of <c>continue</c>: the condition for <c>while</c> and <c>do-while</c>, the
    /// loop head for <c>for-in</c>.</summary>
    public BlockId ContinueTarget => _continue ??= blocks.NewBlock();

    /// <summary>The target of <c>break</c>.</summary>
    public BlockId BreakTarget => _break ??= blocks.NewBlock();

    /// <summary>Has anyone requested the target? Only then does the block exist.</summary>
    public bool ContinueRequested => _continue is not null;

    /// <inheritdoc cref="ContinueRequested"/>
    public bool BreakRequested => _break is not null;
}
