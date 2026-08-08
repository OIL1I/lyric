namespace Lyric.Ir;

/// <summary>
/// Entfernt aus einem gelowerten Modul, was von keiner Wurzel aus erreichbar ist.
///
/// <para><b>Warum es das gibt.</b> Ein Lyric-Rumpf landet im Bytecode, sobald sein Modul geladen
/// ist — auch wenn niemand ihn ruft. Gemessen am 2026-08-08 trug ein Hello-World <b>9 Bytes
/// eigenen Code und 97 Bytes tote Stdlib</b>; mit `std.string` in Lyric kamen dessen Natives
/// (`concat`, `charAt`, `substring`, …) in die Import-Tabelle jedes Programms. Die Stdlib in Lyric
/// zu schreiben ist die richtige Entscheidung — sie braucht nur diesen Pass daneben.</para>
///
/// <para><b>Warum auf der IR und nicht davor.</b> Eine Analyse vor dem Lowering müsste den
/// Aufrufgraph auf AST-Ebene nachbauen, mit Überladungsauflösung, Extensions und
/// Monomorphisierung — ein zweiter Compiler neben dem ersten, und die klassische Stelle, an der
/// zwei Antworten auf dieselbe Frage auseinanderlaufen. Hier stehen die Aufrufe bereits als
/// Instruktionen da. Der Preis ist, dass tote Funktionen erst gelowert und dann verworfen werden:
/// das kostet Compile-Zeit, aber nicht Bytecode — und Bytecode ist, was jeder Start bezahlt.</para>
///
/// <para><b>Formatneutral</b> (ADR-013): weglassen, was niemand ruft, ändert nichts an `.lyrbc`
/// und an keinem Lyric-Programm.</para>
/// </summary>
internal static class Reachability
{
    /// <summary>
    /// Streicht unerreichbare Funktionen und Importe und nummeriert die verbleibenden neu.
    /// </summary>
    /// <remarks>Ohne Einstiegspunkt (Bibliotheks-Modul) passiert <b>nichts</b>: dort ist jede
    /// öffentliche Funktion eine mögliche Wurzel, und welche der Host ruft, weiß der Compiler
    /// nicht. Das stillschweigend zu raten hieße, einem Host Funktionen wegzunehmen, die er
    /// registriert hat.</remarks>
    public static void Prune(IrModule module)
    {
        if (module.EntryFunction is null) return;

        var erreichbar = Collect(module);

        // Alte Id -> neue Id. Die Reihenfolge bleibt erhalten, damit ein Diff zweier Builds
        // lesbar bleibt und die Namen im Bytecode nicht durcheinandergeraten.
        var neueId = new Dictionary<int, int>();
        var behalten = new List<IrFunction>();

        for (var i = 0; i < module.Functions.Count; i++)
        {
            if (!erreichbar.Contains(i)) continue;
            neueId[i] = behalten.Count;
            behalten.Add(module.Functions[i]);
        }

        // Importe genauso: nur die, die eine BEHALTENE Funktion wirklich ruft. Das ist der Teil,
        // den man von aussen sieht — ein toter Rumpf zog bisher seine Natives mit.
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

        // Eine vtable-Zeile, deren Methoden alle gestrichen wurden, ist selbst tot. Zeilen mit
        // gemischtem Zustand darf es nicht geben — Collect nimmt eine Zeile ganz oder gar nicht.
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
    /// Sammelt transitiv, was von den Wurzeln aus erreichbar ist.
    /// </summary>
    /// <remarks>
    /// <para><b>Virtuelle Aufrufe sind der harte Teil.</b> Ein <c>callvirt</c> nennt einen Slot,
    /// keinen Namen — welche Implementierung läuft, steht erst zur Laufzeit fest. Deshalb ist
    /// jede vtable-Zeile, deren Typ überhaupt gelowert wurde, hier eine Wurzel.</para>
    /// <para>Das ist <b>bewusst konservativ</b>: eine schärfere Analyse müsste verfolgen, welche
    /// Typen je durch ein <c>mkiface</c> laufen, und ein Fehler darin wäre ein Programm, das zur
    /// Laufzeit eine fehlende Funktion sucht — der schlechteste Fehler, den ein Compiler machen
    /// kann. Weniger wegzuwerfen als möglich ist der richtige Kompromiss; die freien Funktionen,
    /// um die es geht (<c>parseInt</c>, <c>replace</c>, …), sind ohnehin nicht virtuell.</para>
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

        // Typen, die im erreichbaren Code zu einem Interface-Wert werden. Waechst waehrend der
        // Schleife: ein 'mkiface' kann in einer Funktion stehen, die erst spaeter erreichbar wird.
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

                        // Eine Closure wird nicht am Aufruf sichtbar, sondern hier: 'mkclosure'
                        // nennt die angehobene Funktion, und gerufen wird sie spaeter indirekt.
                        case MakeClosure closure: Wurzel(closure.Target); break;

                        // Der einzige Weg, auf dem ein Interface-Wert im CODE entsteht. Ab hier
                        // kann ein 'callvirt' die Methoden dieses Typs treffen.
                        case MakeInterface iface: gehoben.Add(iface.Concrete.Value); break;
                    }

                // Der Terminator steht NEBEN den Instruktionen und nicht in ihnen — und 'throw'
                // ist der ZWEITE Weg, auf dem ein Typ gehoben wird.
                //
                // Bei einem untypisierten 'catch (e)' baut die VM den Throwable-Fat-Pointer selbst
                // (M8/S4); im Code steht kein 'mkiface'. Ohne diesen Fall strich die Analyse die
                // vtable-Methoden des geworfenen Typs, und das Programm suchte zur Laufzeit eine
                // Implementierung, die es nicht mehr gab — genau der Fehler, den eine
                // Erreichbarkeitsanalyse niemals machen darf. Zwei Tests haben ihn gefangen.
                if (block.Terminator is Throw { Concrete: { } geworfen })
                    gehoben.Add(geworfen.Value);
            }
        }

        return erreichbar;
    }

    /// <summary>
    /// Nimmt die vtable-Methoden der inzwischen gehobenen Typen als neue Wurzeln auf.
    /// </summary>
    /// <remarks>
    /// <para>Hier hängt die Schärfe der Analyse. Ein <c>callvirt</c> nennt einen Slot, keinen
    /// Namen — welche Implementierung läuft, steht erst zur Laufzeit fest. Die entscheidende
    /// Beobachtung: ein Interface-Wert entsteht <b>ausschliesslich</b> durch <c>mkiface</c>. Wer
    /// nie gehoben wird, kann nie virtuell gerufen werden.</para>
    /// <para>Die erste Fassung nahm einfach <b>jede</b> vtable-Zeile als Wurzel. Das war sicher
    /// und wirkungslos: <c>RangeIterator.next</c> blieb in jedem Programm, weil <c>std.iter</c>
    /// geladen war — genau der Fall, um den es geht.</para>
    /// <para>Zurückgegeben wird, ob etwas Neues dazukam: die äussere Schleife muss dann noch
    /// einmal laufen, denn eine neu erreichbare Methode kann selbst weitere Typen heben.</para>
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

    /// <summary>Schreibt Funktions- und Import-Referenzen auf die neuen Indizes um.</summary>
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
