namespace Lyric.Ir;

/// <summary>
/// Replaces a <c>callvirt</c> whose receiver is provably one concrete type with the direct call
/// the dispatch table would have answered.
///
/// <para>The proof is the receiver's definition: a temp is defined exactly once, and when that
/// definition is a <c>mkiface</c>, the concrete type stands IN the instruction. This shape is
/// common after the passes before this one — <c>iter()</c> answers the interface type, inlining
/// puts its <c>mkiface</c> into the caller, and local forwarding hands it to the call site. A
/// receiver that arrives through a parameter or a many-valued local keeps its dispatch.</para>
///
/// <para>The rewrite is CFG-neutral — one instruction for one instruction, the receiver argument
/// becomes the value the <c>mkiface</c> lifted — so it is safe inside handler ranges too. The
/// payoff is not the table lookup: it is that the direct call is now visible to the INLINER, so
/// the pass pipeline runs once more behind this one.</para>
/// </summary>
internal static class Devirtualizer
{
    public static bool Run(IrModule module)
    {
        var changed = false;
        foreach (var function in module.Functions)
        {
            // The single definition of every temp; SSA-light makes this a plain sweep.
            var defs = new Dictionary<int, IrOp>();
            foreach (var block in function.Blocks)
                foreach (var op in block.Insts)
                    if (IrShape.DestOf(op) is { } dest)
                        defs[dest.Value] = op;

            foreach (var block in function.Blocks)
            {
                for (var i = 0; i < block.Insts.Count; i++)
                {
                    if (block.Insts[i] is not CallVirt call || call.Args.Length == 0) continue;
                    if (!defs.TryGetValue(call.Args[0].Value, out var def)) continue;
                    if (def is not MakeInterface lift || lift.Interface != call.Interface)
                        continue;

                    // The row was built by the lowering; a missing one would already be a
                    // dispatch failure at runtime, so absence here just keeps the callvirt.
                    var row = module.Impls.FirstOrDefault(r =>
                        r.Type == lift.Concrete && r.Interface == call.Interface);
                    if (row.Methods is null || call.Slot >= row.Methods.Length) continue;

                    // An overridden slot points at the class's method, whose receiver is the
                    // concrete type — it gets the lifted value. A DEFAULT-method slot points at
                    // the interface's own function, whose receiver is the fat pointer, and which
                    // may dispatch through it again ('this' in a default method is virtual) — it
                    // keeps the interface value.
                    var target = row.Methods[call.Slot];
                    var receiver = module.Functions[target.Value].Locals[0].Type;

                    var args = (TempId[])call.Args.Clone();
                    if (receiver is not IrInterfaceType) args[0] = lift.Value;
                    block.Insts[i] = new Call(call.Dest, target, args, call.Span);
                    changed = true;
                }
            }
        }

        return changed;
    }
}
