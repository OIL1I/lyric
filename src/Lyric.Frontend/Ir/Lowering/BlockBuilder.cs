using Lyric.Core;

namespace Lyric.Ir.Lowering;

/// <summary>
/// An emit cursor over the basic blocks of a function: creates blocks, writes instructions into the
/// current block and seals blocks with their terminator.
///
/// <para>DENSE BLOCK IDS ARE STRUCTURALLY GUARANTEED: <see cref="Append"/> forms the id from
/// <c>Count</c> and appends the block immediately afterwards, so <c>Blocks[i].Id.Value == i</c> always
/// holds. The first block created is <c>bb0</c> and therefore the entry, which gives the second
/// verifier invariant for free.</para>
///
/// <para>WHY <see cref="SealBlock"/> CAN SEAL A FOREIGN BLOCK: an <c>if</c> branch may only jump
/// forward once it is settled that a merge block exists at all, and that is known only after both
/// branches are lowered. By then the cursor has long moved on. The alternative would be to create the
/// merge block eagerly and remove it when unused, which breaks density as soon as the branch created
/// blocks of its own.</para>
///
/// <para>The order of the blocks in the list is creation order rather than control-flow order: for
/// loops the exit block has to exist before the body is lowered, because a <c>break</c> in the body
/// needs its jump target, so it stands before the body blocks. That is correct and dense, but does not
/// read linearly in the dump.</para>
/// </summary>
internal sealed class BlockBuilder
{
    private readonly List<IrBlock> _blocks;
    private IrBlock _current;

    public BlockBuilder(List<IrBlock> blocks)
    {
        _blocks = blocks;
        _current = Append(); // bb0 is the entry
    }

    public BlockId CurrentId => _current.Id;

    /// <summary>Is the current block closed? If so, control flow ends at this point and everything
    /// following is unreachable.</summary>
    public bool IsSealed => _current.Terminator is not null;

    /// <summary>Creates a block WITHOUT moving the cursor.</summary>
    public BlockId NewBlock() => Append().Id;

    public void SwitchTo(BlockId id) => _current = _blocks[id.Value];

    public void Emit(IrOp op)
    {
        if (IsSealed)
            throw new InternalCompilationException(
                $"lowering: cannot emit into sealed block {_current.Id}");
        _current.Insts.Add(op);
    }

    public void Seal(IrTerminator terminator) => SealBlock(_current.Id, terminator);

    public void SealBlock(BlockId id, IrTerminator terminator)
    {
        var block = _blocks[id.Value];
        if (block.Terminator is not null)
            throw new InternalCompilationException($"lowering: block {id} is already sealed");
        block.Terminator = terminator;
    }

    private IrBlock Append()
    {
        var block = new IrBlock(new BlockId(_blocks.Count), new List<IrOp>());
        _blocks.Add(block);
        return block;
    }
}
