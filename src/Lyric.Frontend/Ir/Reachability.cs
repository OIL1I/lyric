namespace Lyric.Ir;

/// <summary>
/// Removes from a lowered module whatever is not reachable from any root.
///
/// <para>A Lyric body reaches the bytecode as soon as its module is loaded, even when nobody calls
/// it. With `std.string` written in Lyric, its natives (`concat`, `charAt`, `substring`, …) end up in
/// the import table of every program.</para>
///
/// <para>ON THE IR RATHER THAN BEFORE IT. An analysis before the lowering would have to rebuild the
/// call graph at AST level, with overload resolution, extensions and monomorphization — a second
/// compiler beside the first. Here the calls already stand as instructions. The price is that dead
/// functions are lowered and then discarded: that costs compile time, but not bytecode, and bytecode
/// is what every start pays for.</para>
///
/// <para>FORMAT-NEUTRAL: omitting what nobody calls changes nothing about `.lyrbc` and nothing about
/// any Lyric program.</para>
/// </summary>
internal static class Reachability
{
    /// <summary>
    /// Deletes unreachable functions and imports and renumbers the remaining ones.
    /// </summary>
    /// <remarks>Without an entry point, meaning a library module, NOTHING happens: there every public
    /// function is a possible root, and which one the host calls is unknown to the compiler.</remarks>
    public static void Prune(IrModule module)
    {
        if (module.EntryFunction is null) return;

        var erreichbar = Collect(module);

        // Old id to new id. The order is preserved, so a diff of two builds stays readable and the
        // names in the bytecode do not get mixed up.
        var neueId = new Dictionary<int, int>();
        var behalten = new List<IrFunction>();

        for (var i = 0; i < module.Functions.Count; i++)
        {
            if (!erreichbar.Contains(i)) continue;
            neueId[i] = behalten.Count;
            behalten.Add(module.Functions[i]);
        }

        // Imports likewise: only those a RETAINED function actually calls. That is the part visible
        // from outside; a dead body used to drag its natives along.
        var benutzteImporte = new SortedSet<int>();
        foreach (var function in behalten)
            foreach (var block in function.Blocks)
                foreach (var op in block.Insts)
                    if (op is CallImport call)
                        benutzteImporte.Add(call.Target.Value);

        var neuerImport = new Dictionary<int, int>();
        var importe = new List<IrImport>();
        foreach (var alt in benutzteImporte)
        {
            neuerImport[alt] = importe.Count;
            importe.Add(module.Imports[alt]);
        }

        Renumber(behalten, neueId, neuerImport);

        module.Functions.Clear();
        module.Functions.AddRange(behalten);
        module.Imports.Clear();
        module.Imports.AddRange(importe);

        module.EntryFunction = new FunctionId(neueId[module.EntryFunction.Value.Value]);
        if (module.GlobalInit is { } init && neueId.TryGetValue(init.Value, out var initNeu))
            module.GlobalInit = new FunctionId(initNeu);

        // The attribute rows follow the renumbering. Their function targets are roots above, so
        // the lookup cannot miss; type targets are untouched, the table keeps every entry.
        for (var i = 0; i < module.Attributes.Count; i++)
        {
            var row = module.Attributes[i];
            if (row.TargetKind == IrAttributeTarget.Function)
                module.Attributes[i] = row with { Target = neueId[row.Target] };
        }

        // A vtable row whose methods were all deleted is dead itself. Rows in a mixed state must not
        // exist: Collect takes a row whole or not at all.
        var impls = module.Impls
            .Where(impl => impl.Methods.All(m => neueId.ContainsKey(m.Value)))
            .Select(impl => impl with
            {
                Methods = impl.Methods.Select(m => new FunctionId(neueId[m.Value])).ToArray(),
            })
            .ToList();

        module.Impls.Clear();
        module.Impls.AddRange(impls);
    }

    /// <summary>
    /// Collects transitively what is reachable from the roots.
    /// </summary>
    /// <remarks>
    /// <para>VIRTUAL CALLS ARE THE HARD PART. A <c>callvirt</c> names a slot, not a name — which
    /// implementation runs is settled only at runtime. Every vtable row whose type was lowered at all is
    /// therefore a root here.</para>
    /// <para>That is DELIBERATELY CONSERVATIVE: a sharper analysis would have to track which types ever
    /// pass through a <c>mkiface</c>, and an error in it would be a program searching for a missing
    /// function at runtime. Throwing away less than possible is the right trade; the free functions this
    /// is about (<c>parseInt</c>, <c>replace</c>, …) are not virtual anyway.</para>
    /// </remarks>
    private static HashSet<int> Collect(IrModule module)
    {
        var erreichbar = new HashSet<int>();
        var offen = new Stack<int>();

        void Wurzel(FunctionId? id)
        {
            if (id is { } f && erreichbar.Add(f.Value)) offen.Push(f.Value);
        }

        Wurzel(module.EntryFunction);
        Wurzel(module.GlobalInit);

        // An attributed function is a root: the row in section 11 is a promise to the host that
        // this function exists, and the host calls it by that index — a caller this analysis
        // cannot see, exactly like the entry point.
        foreach (var attribute in module.Attributes)
            if (attribute.TargetKind == IrAttributeTarget.Function)
                Wurzel(new FunctionId(attribute.Target));

        // Types that become an interface value in reachable code. Grows during the loop: a 'mkiface'
        // may sit in a function that becomes reachable only later.
        var gehoben = new HashSet<int>();
        var impls = module.Impls.Count;

        while (offen.Count > 0 || WeitereImpls(module, gehoben, erreichbar, offen))
        {
            if (offen.Count == 0) continue;

            var current = module.Functions[offen.Pop()];
            foreach (var block in current.Blocks)
            {
                foreach (var op in block.Insts)
                    switch (op)
                    {
                        case Call call: Wurzel(call.Target); break;

                        // A closure becomes visible here rather than at the call: 'mkclosure' names the
                        // lifted function, and it is called indirectly later.
                        case MakeClosure closure: Wurzel(closure.Target); break;

                        // The only way an interface value arises in CODE. From here on a 'callvirt' can
                        // hit the methods of this type.
                        case MakeInterface iface: gehoben.Add(iface.Concrete.Value); break;
                    }

                // The terminator sits BESIDE the instructions rather than inside them, and 'throw' is
                // the SECOND way a type is lifted.
                //
                // For an untyped 'catch (e)' the VM builds the Throwable fat pointer itself; there is no
                // 'mkiface' in the code. Without this case the analysis deletes the vtable methods of the
                // thrown type, and the program searches at runtime for an implementation that no longer
                // exists.
                if (block.Terminator is Throw { Concrete: { } geworfen })
                    gehoben.Add(geworfen.Value);
            }
        }

        return erreichbar;
    }

    /// <summary>
    /// Takes the vtable methods of the types lifted so far as new roots.
    /// </summary>
    /// <remarks>
    /// <para>The sharpness of the analysis hangs here. A <c>callvirt</c> names a slot, not a name —
    /// which implementation runs is settled only at runtime. The decisive observation: an interface
    /// value arises SOLELY through <c>mkiface</c>. What is never lifted can never be called
    /// virtually.</para>
    /// <para>Taking EVERY vtable row as a root would be safe and without effect:
    /// <c>RangeIterator.next</c> would stay in every program, because <c>std.iter</c> is loaded.</para>
    /// <para>Returns whether something new was added: the outer loop then has to run once more, because
    /// a newly reachable method can lift further types itself.</para>
    /// </remarks>
    private static bool WeitereImpls(IrModule module, HashSet<int> gehoben,
        HashSet<int> erreichbar, Stack<int> offen)
    {
        var neu = false;

        foreach (var impl in module.Impls)
        {
            if (!gehoben.Contains(impl.Type.Value)) continue;

            foreach (var method in impl.Methods)
                if (erreichbar.Add(method.Value))
                {
                    offen.Push(method.Value);
                    neu = true;
                }
        }

        return neu;
    }

    /// <summary>Rewrites function and import references to the new indices.</summary>
    private static void Renumber(List<IrFunction> functions,
        IReadOnlyDictionary<int, int> funktionen, IReadOnlyDictionary<int, int> importe)
    {
        foreach (var function in functions)
            foreach (var block in function.Blocks)
                for (var i = 0; i < block.Insts.Count; i++)
                    block.Insts[i] = block.Insts[i] switch
                    {
                        Call call => call with { Target = new FunctionId(funktionen[call.Target.Value]) },
                        MakeClosure c => c with { Target = new FunctionId(funktionen[c.Target.Value]) },
                        CallImport ci => ci with { Target = new ImportId(importe[ci.Target.Value]) },
                        var other => other,
                    };
    }
}
