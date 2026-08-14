using Lyric.Core;
using Lyric.Ir;

namespace Lyric.Bytecode;

/// <summary>Where does a temp's value live while the bytecode runs?</summary>
internal enum Placement
{
    /// <summary>It stays on the operand stack and is taken directly by the next instruction. No
    /// <c>stloc</c> or <c>ldloc</c>.</summary>
    Stack,

    /// <summary>It moves into a local slot: <c>stloc</c> after the definition, <c>ldloc</c> at every
    /// use.</summary>
    Slot,

    /// <summary>It is never read: a <c>pop</c> straight after the definition. In practice only for a
    /// discarded call result (<c>foo();</c> for <c>foo(): int</c>).</summary>
    Discard,
}

/// <summary>The scheduling result for one function.</summary>
internal sealed class FunctionLayout
{
    public required IReadOnlyDictionary<TempId, Placement> Placements { get; init; }
    /// <summary>The slot index of each temp placed in <see cref="Placement.Slot"/>.</summary>
    public required IReadOnlyDictionary<TempId, int> TempSlots { get; init; }
    /// <summary>The type of each slot: the IR locals first (parameters first), then the spilled
    /// Temps.</summary>
    public required IReadOnlyList<IrType> SlotTypes { get; init; }
    public required int MaxStack { get; init; }
}

/// <summary>
/// Translates the temp-based IR into the stack discipline of the VM: which values stay on the
/// operand stack, and which need a local slot?
///
/// <para>The invariant: the stack is empty at every block boundary. Values crossing blocks travel
/// through locals, which the lowering already guarantees structurally. Scheduling is therefore
/// purely block-local, and the stack depth is statically checkable at load time.</para>
///
/// <para>Without scheduling every temp would take a slot, and the disassembly would be full of
/// redundant store/load pairs.</para>
///
/// <para>Correctness never depends on the optimization: the slot path is always available, and
/// scheduling only decides where it may be omitted. It runs optimistically and demotes temps to
/// slots on every collision — monotone, and therefore terminating.</para>
/// </summary>
internal static class StackScheduler
{
    public static FunctionLayout Schedule(IrFunction function)
    {
        var placements = InitialPlacements(function);

        // Simulate optimistically; every collision demotes temps to a slot. Since demotion runs in
        // one direction only (stack to slot) and there are finitely many temps, this terminates.
        int maxStack;
        while (!TrySimulate(function, placements, out maxStack)) { }

        var slotTypes = new List<IrType>(function.Locals.Select(l => l.Type));
        var tempSlots = new Dictionary<TempId, int>();
        foreach (var temp in function.Temps) // by TempId, and therefore deterministic
        {
            if (placements[temp.Id] != Placement.Slot) continue;
            tempSlots[temp.Id] = slotTypes.Count;
            slotTypes.Add(temp.Type);
        }

        return new FunctionLayout
        {
            Placements = placements,
            TempSlots = tempSlots,
            SlotTypes = slotTypes,
            MaxStack = maxStack,
        };
    }

    /// <summary>
    /// A first approximation from counting alone: a temp may live on the stack only when it is read
    /// EXACTLY ONCE and IN THE SAME BLOCK. Multiple uses would not work, because the stack
    /// consumes; nor would a cross-block read, because the stack is empty at the boundary
    /// there. No reader means <see cref="Placement.Discard"/>.
    /// </summary>
    private static Dictionary<TempId, Placement> InitialPlacements(IrFunction function)
    {
        var useCount = new Dictionary<TempId, int>();
        var useBlock = new Dictionary<TempId, BlockId>();
        var defBlock = new Dictionary<TempId, BlockId>();

        foreach (var block in function.Blocks)
        {
            foreach (var op in block.Insts)
            {
                foreach (var operand in IrShape.OperandsOf(op)) Record(operand, block.Id);
                if (IrShape.DestOf(op) is { } dest) defBlock[dest] = block.Id;
            }
            foreach (var operand in IrShape.OperandsOf(block.Terminator!)) Record(operand, block.Id);
        }

        var placements = new Dictionary<TempId, Placement>();
        foreach (var temp in function.Temps)
        {
            var count = useCount.GetValueOrDefault(temp.Id);
            placements[temp.Id] =
                count == 0 ? Placement.Discard
                : count == 1 && defBlock.TryGetValue(temp.Id, out var def)
                             && useBlock[temp.Id] == def ? Placement.Stack
                : Placement.Slot;
        }
        return placements;

        void Record(TempId temp, BlockId block)
        {
            useCount[temp] = useCount.GetValueOrDefault(temp) + 1;
            useBlock[temp] = block;
        }
    }

    /// <summary>
    /// Simulates the operand stack block by block. Returns false as soon as temps were demoted;
    /// another pass is then needed.
    /// </summary>
    private static bool TrySimulate(IrFunction function,
        Dictionary<TempId, Placement> placements, out int maxStack)
    {
        maxStack = 0;

        foreach (var block in function.Blocks)
        {
            var stack = new List<TempId>();

            foreach (var op in block.Insts)
            {
                if (!Consume(IrShape.OperandsOf(op), stack, placements, ref maxStack)) return false;

                if (IrShape.DestOf(op) is not { } dest) continue;
                switch (placements[dest])
                {
                    case Placement.Stack:
                        stack.Add(dest);
                        maxStack = Math.Max(maxStack, stack.Count);
                        break;
                    case Placement.Discard:
                        // sits on top briefly and is popped immediately
                        maxStack = Math.Max(maxStack, stack.Count + 1);
                        break;
                    case Placement.Slot:
                        maxStack = Math.Max(maxStack, stack.Count + 1); // up to the stloc
                        break;
                }
            }

            if (!Consume(IrShape.OperandsOf(block.Terminator!), stack, placements, ref maxStack))
                return false;

            // After the terminator the stack has to be empty. A remainder would mean a temp lies
            // on the stack with no reader, which Placement.Discard rules out.
            if (stack.Count != 0)
                throw new InternalCompilationException(
                    $"stack-scheduler: {function.Name}: {stack.Count} value(s) left on the stack " +
                    $"at the end of {block.Id}");
        }

        return true;
    }

    /// <summary>
    /// Consumes an instruction's operands. Three cases, and only three:
    /// <list type="bullet">
    /// <item>all operands lie as a SUFFIX of the stack in exactly this order: they are popped and no
    /// loads arise;</item>
    /// <item>no operand is on the stack: all arrive through <c>ldloc</c> on top and are
    /// consumed immediately, while anything below stays untouched;</item>
    /// <item>anything else (mixed, or on the stack in the wrong position): not emittable, because
    /// an <c>ldloc</c> above an operand already there would destroy the order. The affected temps
    /// are demoted to slots.</item>
    /// </list>
    /// </summary>
    private static bool Consume(IReadOnlyList<TempId> operands, List<TempId> stack,
        Dictionary<TempId, Placement> placements, ref int maxStack)
    {
        if (operands.Count == 0) return true;

        var onStack = operands.Count(o => placements[o] == Placement.Stack);

        if (onStack == 0)
        {
            maxStack = Math.Max(maxStack, stack.Count + operands.Count);
            return true;
        }

        if (onStack == operands.Count && EndsWith(stack, operands))
        {
            maxStack = Math.Max(maxStack, stack.Count);
            stack.RemoveRange(stack.Count - operands.Count, operands.Count);
            return true;
        }

        // Collision: demote and simulate again. The operands suffice — everything else on the
        // stack is deeper by construction and stays valid.
        foreach (var operand in operands)
            if (placements[operand] == Placement.Stack)
                placements[operand] = Placement.Slot;

        return false;
    }

    private static bool EndsWith(List<TempId> stack, IReadOnlyList<TempId> operands)
    {
        if (stack.Count < operands.Count) return false;
        var offset = stack.Count - operands.Count;
        for (var i = 0; i < operands.Count; i++)
            if (stack[offset + i] != operands[i]) return false;
        return true;
    }
}
