using System.Globalization;
using Lyric.Core;

namespace Lyric.Ir;

/// <summary>
/// Prüft ein <see cref="IrModule"/> auf Wohlgeformtheit. Der Verifier ist eine Assertion-Suite
/// über die IR-Datenstruktur, <b>kein</b> Type-Checker: er beantwortet „ist diese IR wohlgeformt",
/// nicht „ist das Programm des Users korrekt". Sprachregeln durchsetzt die Sema (M3/M4); sie hier
/// zu wiederholen wäre ein Parallel-Mechanismus. Insbesondere prüft der Verifier <b>nicht</b>,
/// ob Locals vor dem ersten Read zugewiesen sind — das hat die Definite-Assignment-Analyse bewiesen.
///
/// Jeder Befund ist damit ein <b>Compiler-Bug</b> (Lowering oder Monomorphisierung), keine
/// User-Diagnose: deshalb Klartext-Strings statt <c>LYR-IR####</c>-Codes. Der Bereich
/// <c>LYR-IR####</c> bleibt echten, user-sichtbaren Lowering-Fehlern vorbehalten.
///
/// <para><b>Warum sammeln statt beim ersten Fehler abbrechen</b>: ein Lowering-Bug äußert sich
/// typisch in mehreren Symptomen (falscher Temp-Typ → Dest-Mismatch → Return-Mismatch). Alle
/// gleichzeitig zu sehen zeigt die verantwortliche Stelle; das erste allein nicht.</para>
///
/// <para><b>Phasen und Bail-out</b>: Prüfungen setzen einander voraus — mit einer Lücke in der
/// Temp-Tabelle greift jeder Typ-Lookup daneben, mit doppelten Block-Ids ist die Ziel-Auflösung
/// Raten, mit zwei Definitionen pro Temp ist die Availability-Analyse bedeutungslos. Darum vier
/// Phasen, die bei Fundamentalfehlern die Funktion abbrechen (die nächste wird normal geprüft).
/// Dasselbe Prinzip wie <c>ErrorType</c> als Poison in der Sema: keine Folgefehler.</para>
///
/// <para>Der Verifier läuft nach dem Lowering und vor der Bytecode-Emission; in Tests und
/// Debug-Builds immer, im Release hinter einem Flag (Vorbild: LLVMs Verifier in Assert-Builds).</para>
/// </summary>
public static class IrVerifier
{
    /// <summary>Prüft das Modul und liefert alle Befunde. Leere Liste = wohlgeformt.
    /// Die Reihenfolge ist deterministisch (Deklarations-Reihenfolge in Phase 0/1,
    /// Reverse-Postorder in Phase 2/3).</summary>
    public static IReadOnlyList<string> Verify(IrModule module)
    {
        var findings = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        VerifyTypes(module, findings);
        VerifyImpls(module, findings);

        // Die Init-Funktion wird von der Runtime vor dem Einstiegspunkt gerufen; ein Index ins
        // Leere waere dort erst beim Laden aufgefallen.
        if (module.GlobalInit is { } init
            && (init.Value < 0 || init.Value >= module.Functions.Count))
            findings.Add($"global initializer {init} is out of range " +
                         $"(module has {module.Functions.Count} function(s))");

        // Globals ohne Initialisierer waeren uninitialisierte Slots — und jeder Wert in Lyric hat
        // einen (§6.6). Entweder gibt es beide oder keines.
        if (module.Globals.Count > 0 && module.GlobalInit is null)
            findings.Add($"module declares {module.Globals.Count} global(s) but no initializer");

        foreach (var function in module.Functions)
        {
            // Namen sind die Symbol-Namen im Bytecode (ADR-013). Eine Kollision ist der
            // Kanarienvogel für die Monomorphisierung: zwei Instanzen, die auf denselben
            // gemangelten Namen fallen, wären ein stiller Falsch-Call.
            if (!seenNames.Add(function.Name))
                findings.Add($"{function.Name}: duplicate function name");

            new FunctionVerifier(module, function, findings).Run();
        }

        // Der Einstiegspunkt wird zur Start-Sektion im Bytecode. Ein Index ins Leere wäre dort
        // erst beim Laden aufgefallen — also hier prüfen, wo er entsteht.
        if (module.EntryFunction is { } entry)
        {
            if (entry.Value < 0 || entry.Value >= module.Functions.Count)
                findings.Add($"entry function {entry} is out of range " +
                             $"(module has {module.Functions.Count} function(s))");
            else if (module.Functions[entry.Value].ParamCount != 0)
                findings.Add($"entry function {module.Functions[entry.Value].Name} takes " +
                             "parameters; the no-argument form is the only one lowered today");
        }

        return findings;
    }

    /// <summary>
    /// Prüft die Typ-Tabelle, bevor irgendeine Funktion sie benutzt. Vorgezogen aus demselben
    /// Grund, aus dem die Funktions-Phasen einen Bail-out haben: läuft eine Instruktion gegen ein
    /// kaputtes Layout, ist ihr Befund Folge des Tabellen-Fehlers und nicht seine Ursache.
    ///
    /// <para><b>Rekursion ist ausdrücklich erlaubt</b> — <c>class Node { next: Node }</c> ist
    /// gültig, auch vorwärts. Deshalb prüft diese Schleife nur Bereichsgrenzen und läuft dem
    /// Feldtyp nicht nach; genau dafür trägt <see cref="IrRefType"/> nur die Id.</para>
    /// </summary>
    private static void VerifyTypes(IrModule module, List<string> findings)
    {
        for (var i = 0; i < module.Types.Count; i++)
        {
            var def = module.Types[i];
            var id = new TypeId(i);

            if (def.FieldTypes.Length != def.FieldNames.Length)
            {
                findings.Add($"type {id} '{def.Name}' has {def.FieldTypes.Length} field type(s) " +
                             $"but {def.FieldNames.Length} name(s)");
                continue;
            }

            // Ein Enum-Eintrag traegt keine eigenen Felder — seine Varianten tun das. Jede muss ein
            // Layout sein und darf nicht selbst wieder ein Enum sein.
            for (var v = 0; v < def.Variants.Length; v++)
            {
                var variant = def.Variants[v];
                if (variant.Value < 0 || variant.Value >= module.Types.Count)
                    findings.Add($"enum {id} '{def.Name}': variant {v} references type {variant}, " +
                                 $"which is out of range (module has {module.Types.Count} type(s))");
                else if (module.Types[variant.Value].IsEnum)
                    findings.Add($"enum {id} '{def.Name}': variant {v} is itself an enum");
                else if (module.Types[variant.Value].FieldTypes.Length == 0)
                    findings.Add($"enum {id} '{def.Name}': variant {v} has no tag slot");
            }

            if (def.IsEnum && def.FieldTypes.Length > 0)
                findings.Add($"enum {id} '{def.Name}' must not have fields of its own; " +
                             "its variants carry them");

            for (var f = 0; f < def.FieldTypes.Length; f++)
            {
                switch (def.FieldTypes[f])
                {
                    // void ist ausschließlich Rückgabetyp (Bytecode.md §3). Ein void-Feld hätte
                    // keine Breite und keinen Nullwert — es ist kein Wert.
                    case IrScalarType { Kind: IrScalar.Void }:
                        findings.Add($"type {id} '{def.Name}': field {new FieldId(f)} " +
                                     $"'{def.FieldNames[f]}' is void");
                        break;

                    case IrRefType r when r.Type.Value < 0 || r.Type.Value >= module.Types.Count:
                        findings.Add($"type {id} '{def.Name}': field {new FieldId(f)} " +
                                     $"'{def.FieldNames[f]}' references type {r.Type}, which is out " +
                                     $"of range (module has {module.Types.Count} type(s))");
                        break;

                    // Ein Array-Feld trägt seinen Elementtyp inline; eine Referenz darin muss
                    // genauso in die Tabelle zeigen wie eine direkte.
                    case IrArrayType arr when Innermost(arr) is IrRefType inner
                                              && (inner.Type.Value < 0 || inner.Type.Value >= module.Types.Count):
                        findings.Add($"type {id} '{def.Name}': field {new FieldId(f)} " +
                                     $"'{def.FieldNames[f]}' has element type {inner.Type}, which is " +
                                     $"out of range (module has {module.Types.Count} type(s))");
                        break;
                }
            }
        }
    }

    /// <summary>Schält Array-Schichten ab: <c>int[][]</c> → <c>int</c>. Terminiert, weil ein
    /// Array-Typ seinen Elementtyp inline trägt und damit endlich tief ist.</summary>
    private static IrType Innermost(IrType type)
    {
        while (type is IrArrayType a) type = a.Element;
        return type;
    }

    /// <summary>Wie <see cref="Verify"/>, wirft aber bei Befunden. Für Aufrufstellen im Lowering,
    /// die von wohlgeformter IR ausgehen dürfen.</summary>
    /// <remarks>Bewusst <b>ohne</b> IR-Dump in der Nachricht: <see cref="IrPrinter"/> wirft selbst
    /// bei fehlendem Terminator und würde damit genau den Befund verdecken, den wir melden wollen.</remarks>
    /// <summary>
    /// Die Impl-Tabelle: jede Zeile nennt existierende Typen, ihr Interface ist wirklich eines,
    /// ihre Klasse ist keines, die Zeile hat genau so viele Eintraege wie das Interface Slots,
    /// jede Zielfunktion existiert und nimmt einen Empfaenger — und es gibt kein Paar zweimal.
    ///
    /// <para>Diese Zeilen werden im Bytecode zur vtable. Ein Fehler darin ist ein Aufruf der
    /// falschen Funktion mit den richtigen Argumenten — die Sorte Bug, die weit weg von ihrer
    /// Ursache auffaellt. Deshalb hier und nicht erst im Reader.</para>
    /// </summary>
    private static void VerifyImpls(IrModule module, List<string> findings)
    {
        var seen = new HashSet<(int Type, int Interface)>();

        for (var i = 0; i < module.Impls.Count; i++)
        {
            var impl = module.Impls[i];
            var where = $"impl #{i} ({impl.Type} :: {impl.Interface})";

            if (impl.Type.Value < 0 || impl.Type.Value >= module.Types.Count
                || impl.Interface.Value < 0 || impl.Interface.Value >= module.Types.Count)
            {
                findings.Add($"{where}: references a type outside the table " +
                             $"(module has {module.Types.Count} type(s))");
                continue;
            }

            var iface = module.Types[impl.Interface.Value];
            if (!iface.IsInterface)
            {
                findings.Add($"{where}: {iface.Name} is not an interface");
                continue;
            }

            if (module.Types[impl.Type.Value].IsInterface)
            {
                findings.Add($"{where}: an interface cannot implement another interface");
                continue;
            }

            if (!seen.Add((impl.Type.Value, impl.Interface.Value)))
            {
                findings.Add($"{where}: duplicate impl row — the dispatch would be ambiguous");
                continue;
            }

            if (impl.Methods.Length != iface.MethodSlots.Length)
            {
                findings.Add($"{where}: has {impl.Methods.Length} method(s) but {iface.Name} " +
                             $"declares {iface.MethodSlots.Length} slot(s)");
                continue;
            }

            for (var slot = 0; slot < impl.Methods.Length; slot++)
            {
                var target = impl.Methods[slot];
                if (target.Value < 0 || target.Value >= module.Functions.Count)
                {
                    findings.Add($"{where}: slot {slot} ({iface.MethodSlots[slot]}) targets " +
                                 $"{target}, which is out of range");
                    continue;
                }

                // Der Empfaenger ist Parameter 0 (ADR-014). Eine Zielfunktion ohne Parameter
                // koennte ihn nicht entgegennehmen — das waere ein 'static' in einer vtable.
                if (module.Functions[target.Value].ParamCount == 0)
                    findings.Add($"{where}: slot {slot} ({iface.MethodSlots[slot]}) targets " +
                                 $"{module.Functions[target.Value].Name}, which takes no receiver");
            }
        }
    }

    public static void VerifyOrThrow(IrModule module)
    {
        var findings = Verify(module);
        if (findings.Count == 0) return;

        throw new InternalCompilationException(
            $"ir-verifier: malformed IR ({findings.Count} finding(s))\n  " +
            string.Join("\n  ", findings));
    }

    /// <summary>
    /// Verifikations-Kontext <b>einer</b> Funktion: lebt für die Dauer ihrer Prüfung und stirbt
    /// dann. Ein Objekt statt statischer Methoden, weil die abgeleiteten Tabellen (Block-Map,
    /// Preds, Reachability, Availability) einmal berechnet und von allen Checks geteilt werden.
    /// Pro Funktion statt pro Modul, weil Temp-/Local-/Block-Ids in jeder Funktion bei 0 starten
    /// und alle Tabellen damit funktionslokal sind. (Form wie LLVMs <c>Verifier</c> und rustcs
    /// <c>CfgChecker</c>/<c>TypeChecker</c>.)
    ///
    /// Traversierung per <c>switch</c>, nicht per Visitor — wie <see cref="IrPrinter"/> und aus
    /// demselben Grund: der <c>default</c>-Wurf erzwingt Vollständigkeit, sobald eine neue
    /// Instruktion hinzukommt. Ein unbekannter Instruktionstyp ist dabei <b>kein</b> Befund,
    /// sondern ein Wurf: „der Verifier ist veraltet" ist eine andere Bug-Klasse als „die IR ist kaputt".
    /// </summary>
    private sealed class FunctionVerifier
    {
        private readonly IrModule _module; // nur für die Auflösung von Call-Zielen
        private readonly IrFunction _fn;
        private readonly List<string> _findings;

        // Phase 0
        private readonly Dictionary<TempId, string> _defSite = new();
        // Phase 1
        private readonly Dictionary<BlockId, IrBlock> _blockById = new();
        private readonly Dictionary<BlockId, List<BlockId>> _preds = new();
        private readonly Dictionary<BlockId, List<BlockId>> _succs = new();
        // Phase 2
        private readonly HashSet<BlockId> _reachable = new();
        private List<BlockId> _rpo = new();
        private readonly Dictionary<BlockId, HashSet<TempId>> _defs = new();
        private readonly Dictionary<BlockId, HashSet<TempId>> _availIn = new();
        private readonly Dictionary<BlockId, HashSet<TempId>> _availOut = new();

        public FunctionVerifier(IrModule module, IrFunction function, List<string> findings)
        {
            _module = module;
            _fn = function;
            _findings = findings;
        }

        public void Run()
        {
            if (!CheckTables()) return;
            if (!CheckHandlers()) return;
            if (!CheckCfgShape()) return;
            ComputeReachabilityAndAvailability();
            CheckInstructions();
        }

        /// <summary>
        /// Die geschuetzten Regionen. Laeuft <b>vor</b> der CFG-Pruefung, weil die Reachability
        /// sie als Wurzeln benutzt — ein Bereich ins Leere wuerde dort sonst danebengreifen.
        /// </summary>
        private bool CheckHandlers()
        {
            var ok = true;
            var count = _fn.Blocks.Count;

            for (var i = 0; i < _fn.Handlers.Count; i++)
            {
                var h = _fn.Handlers[i];
                var where = $"handler #{i}";

                if (h.Start.Value < 0 || h.End.Value > count || h.Start.Value >= h.End.Value)
                {
                    Report($"{where}: protected range [{h.Start}, {h.End}) is not a valid " +
                           $"block range (function has {count} block(s))");
                    ok = false;
                    continue;
                }

                if (h.Handler.Value < 0 || h.Handler.Value >= count)
                {
                    Report($"{where}: handler block {h.Handler} is out of range");
                    ok = false;
                    continue;
                }

                // Ein Handler, der sich selbst schuetzt, waere eine Endlosschleife beim Abwickeln:
                // sein eigener Wurf faende wieder ihn.
                if (h.Handler.Value >= h.Start.Value && h.Handler.Value < h.End.Value)
                {
                    Report($"{where}: handler block {h.Handler} lies inside its own protected " +
                           $"range [{h.Start}, {h.End}) — unwinding would not terminate");
                    ok = false;
                }

                if (h.Kind == IrHandlerKind.Finally && (h.CatchType is not null || h.Slot is not null))
                {
                    Report($"{where}: a finally region catches nothing and binds nothing");
                    ok = false;
                }

                if (h.Slot is { } slot && (slot.Value < 0 || slot.Value >= _fn.Locals.Count))
                {
                    Report($"{where}: binds into slot {slot}, which is outside the local table");
                    ok = false;
                }
            }

            return ok;
        }

        // ------------------------------------------------------------------ Phase 0: Tabellen

        /// <summary>Tabellen-Invarianten. Liefert false, wenn ab hier kein Lookup mehr sicher ist.</summary>
        private bool CheckTables()
        {
            var ok = true;

            // Dichte Tabellen sind keine Kosmetik: die Id IST der Slot-Index im Bytecode. Eine
            // Lücke oder Permutation äußert sich sonst als Falsch-Slot-Read in der VM.
            for (var i = 0; i < _fn.Locals.Count; i++)
            {
                var local = _fn.Locals[i];
                if (local.Id.Value != i)
                {
                    Report($"locals table not dense at index {N(i)}: found {local.Id}");
                    ok = false;
                }

                // Es gibt keine void-Werte; void ist nur ein Funktions-Rückgabetyp (Sprache.md §4).
                if (IsVoid(local.Type))
                {
                    Report($"local {local.Id} ({local.Name}) has type void");
                    ok = false;
                }
            }

            for (var i = 0; i < _fn.Temps.Count; i++)
            {
                var temp = _fn.Temps[i];
                if (temp.Id.Value != i)
                {
                    Report($"temps table not dense at index {N(i)}: found {temp.Id}");
                    ok = false;
                }

                if (IsVoid(temp.Type))
                {
                    Report($"temp {temp.Id} has type void");
                    ok = false;
                }
            }

            // Konvention: die ersten ParamCount Locals SIND die Parameter, in Reihenfolge. Ohne
            // sie trägt die IR keine Parameter-Typen und ein Call ist nicht typprüfbar.
            if (_fn.ParamCount < 0 || _fn.ParamCount > _fn.Locals.Count)
            {
                Report($"paramCount {N(_fn.ParamCount)} out of range (locals: {N(_fn.Locals.Count)})");
                ok = false;
            }

            if (!ok) return false; // ab hier hängt alles an TypeOf/LocalTypeOf

            // Genau eine Definition pro Temp — das ist die "SSA-light"-Zusage, und ohne sie ist
            // jede Def/Use-Argumentation in Phase 3 wertlos. Läuft über ALLE Blöcke, auch
            // unerreichbare: ein zweimal definiertes Temp ist auch in totem Code malformed.
            foreach (var block in _fn.Blocks)
            {
                for (var i = 0; i < block.Insts.Count; i++)
                {
                    if (IrShape.DestOf(block.Insts[i]) is not { } dest) continue;

                    if (!IsKnownTemp(dest))
                    {
                        Report(block.Id, i, $"dest {dest} is not in the temp table");
                        ok = false;
                    }
                    else if (!_defSite.TryAdd(dest, $"{block.Id}: #{N(i)}"))
                    {
                        Report(block.Id, i, $"{dest} is defined more than once " +
                                            $"(first at {_defSite[dest]})");
                        ok = false;
                    }
                }
            }

            // Ein deklariertes, nie definiertes Temp reserviert einen VM-Slot für nichts.
            // (Der umgekehrte Fall — definiert, nie benutzt — ist legal: eine verworfene
            // Call-Rückgabe wie `foo();` bei `foo(): int`.)
            foreach (var temp in _fn.Temps)
            {
                if (!_defSite.ContainsKey(temp.Id))
                {
                    Report($"{temp.Id} is declared in the temp table but never defined");
                    ok = false;
                }
            }

            return ok;
        }

        // ------------------------------------------------------------------ Phase 1: CFG-Form

        /// <summary>CFG-Form und Prädecessor-Tabelle. Liefert false, wenn Reachability und
        /// Availability nicht berechenbar wären.</summary>
        private bool CheckCfgShape()
        {
            if (_fn.Blocks.Count == 0)
            {
                Report("no blocks");
                return false;
            }

            var ok = true;

            for (var i = 0; i < _fn.Blocks.Count; i++)
            {
                var block = _fn.Blocks[i];
                if (!_blockById.TryAdd(block.Id, block))
                {
                    Report($"duplicate block id {block.Id}");
                    return false; // ohne eindeutige Ids ist jede Ziel-Auflösung Raten
                }

                if (block.Id.Value != i)
                {
                    Report($"block table not dense at index {N(i)}: found {block.Id}");
                    ok = false;
                }
            }

            if (!_blockById.ContainsKey(_fn.Entry))
            {
                Report($"entry block {_fn.Entry} does not exist");
                return false;
            }

            if (_fn.Entry != _fn.Blocks[0].Id)
            {
                Report($"entry is {_fn.Entry}, expected the first block {_fn.Blocks[0].Id}");
                ok = false;
            }

            foreach (var block in _fn.Blocks)
            {
                if (block.Terminator is null)
                {
                    Report($"{block.Id}: has no terminator");
                    return false; // ohne Terminator keine Successors -> Reachability wäre gelogen
                }
            }

            if (!ok) return false;

            foreach (var block in _fn.Blocks)
            {
                _preds[block.Id] = new List<BlockId>();
                _succs[block.Id] = new List<BlockId>();
            }

            foreach (var block in _fn.Blocks)
            {
                foreach (var target in IrShape.SuccessorsOf(block.Terminator!))
                {
                    if (!_blockById.ContainsKey(target))
                    {
                        ReportTerm(block.Id, $"branches to unknown block {target}");
                        ok = false;
                        continue;
                    }

                    _succs[block.Id].Add(target);
                    _preds[target].Add(block.Id);
                }
            }

            if (!ok) return false;

            // Der Entry ist der einzige Ort für Parameter-Setup; ein Rücksprung dorthin würde es
            // wiederholen. Kein Bail-out: availIn[entry] bleibt fest leer, was für die Analyse
            // korrekt ist (beim ersten Durchlauf ist dort tatsächlich kein Temp definiert).
            if (_preds[_fn.Entry].Count > 0)
            {
                Report($"entry {_fn.Entry} has predecessors " +
                       string.Join(", ", _preds[_fn.Entry]));
            }

            return true;
        }

        // ---------------------------------------------- Phase 2: Reachability + Availability

        private void ComputeReachabilityAndAvailability()
        {
            ComputeReachabilityAndOrder();

            foreach (var block in _fn.Blocks)
            {
                if (!_reachable.Contains(block.Id))
                    Report($"{block.Id}: unreachable from entry {_fn.Entry}");
            }

            ComputeAvailability();
        }

        /// <summary>Iterativer DFS über die Successors: sammelt Reachability und Postorder.
        /// Iterativ statt rekursiv, weil tief verschachtelte Blöcke sonst den CLR-Stack reißen.</summary>
        private void ComputeReachabilityAndOrder()
        {
            var postorder = new List<BlockId>();
            var stack = new Stack<(BlockId Block, int NextSuccessor)>();

            _reachable.Add(_fn.Entry);
            stack.Push((_fn.Entry, 0));

            // Handler-Bloecke sind zusaetzliche Wurzeln. Sie haben im CFG keinen Praedecessor —
            // erreicht werden sie ueber die Handler-Tabelle beim Abwickeln, nicht ueber einen
            // Sprung. Ohne sie hier zu verankern meldete der Verifier jeden catch-Block als
            // unerreichbar, und die Regel „unerreichbare Bloecke sind ein Fehler" (P4) wuerde
            // Exceptions unmoeglich machen statt sie zu pruefen.
            foreach (var handler in _fn.Handlers)
            {
                if (handler.Handler.Value < 0 || handler.Handler.Value >= _fn.Blocks.Count) continue;
                if (_reachable.Add(handler.Handler)) stack.Push((handler.Handler, 0));
            }

            while (stack.Count > 0)
            {
                var (block, next) = stack.Pop();
                var successors = _succs[block];

                if (next < successors.Count)
                {
                    stack.Push((block, next + 1));
                    var child = successors[next];
                    if (_reachable.Add(child)) stack.Push((child, 0));
                }
                else
                {
                    postorder.Add(block);
                }
            }

            postorder.Reverse();
            _rpo = postorder; // Reverse-Postorder: Prädecessoren fast immer vor ihrem Block
        }

        /// <summary>
        /// Availability: welche Temps sind am Blockeingang auf <b>jedem</b> Pfad schon definiert?
        /// Vorwärts-Dataflow mit Schnittmenge als Meet. Bei genau einer Definition pro Temp
        /// (Phase 0) ist „auf jedem Pfad verfügbar" äquivalent zu „die Definition dominiert den
        /// Use" — deshalb braucht der Verifier keinen Dominator-Baum. Der wird erst interessant,
        /// wenn Phi-Knoten dazukommen.
        /// </summary>
        private void ComputeAvailability()
        {
            var allTemps = new HashSet<TempId>(_fn.Temps.Select(t => t.Id));

            foreach (var block in _rpo)
            {
                _defs[block] = new HashSet<TempId>();
                foreach (var op in _blockById[block].Insts)
                    if (IrShape.DestOf(op) is { } dest) _defs[block].Add(dest);
            }

            foreach (var block in _rpo)
            {
                // Optimistisches TOP für alles außer Entry: ein Loop-Header wird über seine
                // Back-Edge zuerst gegen ein noch nicht finales availOut geschnitten.
                // Pessimistisch (leere Menge) zu starten würde auf zu kleine Mengen konvergieren
                // und echte use-before-def-Fehler verstecken.
                _availIn[block] = block == _fn.Entry
                    ? new HashSet<TempId>() // Parameter sind LOCALS, keine Temps
                    : new HashSet<TempId>(allTemps);
                _availOut[block] = Union(_availIn[block], _defs[block]);
            }

            bool changed;
            do
            {
                changed = false;
                foreach (var block in _rpo)
                {
                    if (block != _fn.Entry)
                    {
                        var incoming = MeetOfPredecessors(block);
                        if (!incoming.SetEquals(_availIn[block]))
                        {
                            _availIn[block] = incoming;
                            changed = true;
                        }
                    }

                    var outgoing = Union(_availIn[block], _defs[block]);
                    if (!outgoing.SetEquals(_availOut[block]))
                    {
                        _availOut[block] = outgoing;
                        changed = true;
                    }
                }
            } while (changed); // monoton fallende Mengen über endlicher Grundmenge -> terminiert
        }

        private HashSet<TempId> MeetOfPredecessors(BlockId block)
        {
            HashSet<TempId>? intersection = null;

            foreach (var pred in _preds[block])
            {
                // Unerreichbare Prädecessoren müssen raus: ihr availOut ist nie stabilisiert
                // worden und würde die Schnittmenge verfälschen.
                if (!_reachable.Contains(pred)) continue;

                if (intersection is null) intersection = new HashSet<TempId>(_availOut[pred]);
                else intersection.IntersectWith(_availOut[pred]);
            }

            // Kann für erreichbare Nicht-Entry-Blöcke nicht eintreten (sie haben per Definition
            // einen erreichbaren Prädecessor); die leere Menge ist der konservative Fallback.
            return intersection ?? new HashSet<TempId>();
        }

        private static HashSet<TempId> Union(HashSet<TempId> a, HashSet<TempId> b)
        {
            var result = new HashSet<TempId>(a);
            result.UnionWith(b);
            return result;
        }

        // -------------------------------------------------- Phase 3: Def/Use und Typen

        private void CheckInstructions()
        {
            foreach (var blockId in _rpo) // nur erreichbare Blöcke
            {
                var block = _blockById[blockId];

                // live wächst instruktionsweise mit; damit fällt "im selben Block, aber textuell
                // nach dem Use definiert" ohne Sonderfall auf.
                var live = new HashSet<TempId>(_availIn[blockId]);

                for (var i = 0; i < block.Insts.Count; i++)
                {
                    var op = block.Insts[i];
                    if (CheckOperands(IrShape.OperandsOf(op), live, blockId, i))
                        CheckOpTypes(op, blockId, i);
                    if (IrShape.DestOf(op) is { } dest) live.Add(dest);
                }

                var terminator = block.Terminator!;
                if (CheckOperands(IrShape.OperandsOf(terminator), live, blockId, index: null))
                    CheckTerminatorTypes(terminator, blockId);
            }
        }

        /// <summary>Prüft, dass jeder Operand ein bekanntes und an dieser Stelle bereits
        /// definiertes Temp ist. Liefert false, wenn ein Operand unbekannt ist — dann sind die
        /// Typ-Checks nicht ausführbar, weil der Tabellen-Lookup daneben greifen würde.</summary>
        private bool CheckOperands(IReadOnlyList<TempId> operands, HashSet<TempId> live,
            BlockId block, int? index)
        {
            var usable = true;

            foreach (var operand in operands)
            {
                if (!IsKnownTemp(operand))
                {
                    ReportAt(block, index, $"uses {operand}, which is not in the temp table");
                    usable = false;
                }
                else if (!live.Contains(operand))
                {
                    var site = _defSite.TryGetValue(operand, out var where) ? $" (defined at {where})" : "";
                    ReportAt(block, index, $"uses {operand} before its definition{site}");
                }
            }

            return usable;
        }

        private void CheckOpTypes(IrOp op, BlockId block, int index)
        {
            switch (op)
            {
                case Const c: CheckConst(c, block, index); break;
                case BinOp b: CheckBinOp(b, block, index); break;
                case UnOp u: CheckUnOp(u, block, index); break;
                case Convert cv: CheckConvert(cv, block, index); break;
                case LoadLocal l: CheckLoadLocal(l, block, index); break;
                case StoreLocal s: CheckStoreLocal(s, block, index); break;
                case Call k: CheckCall(k, block, index); break;
                case CallImport k: CheckCallImport(k, block, index); break;
                case NewObject n: CheckNewObject(n, block, index); break;
                case LoadField f: CheckLoadField(f, block, index); break;
                case StoreField f: CheckStoreField(f, block, index); break;
                case NewArray a: CheckNewArray(a, block, index); break;
                case LoadElem e: CheckLoadElem(e, block, index); break;
                case StoreElem e: CheckStoreElem(e, block, index); break;
                case ArrayLen a: CheckArrayLen(a, block, index); break;
                case ArrayConcat c: CheckArrayConcat(c, block, index); break;
                case ArrayRepeat r: CheckArrayRepeat(r, block, index); break;
                case OptNone n: CheckOptNone(n, block, index); break;
                case OptSome s: CheckOptSome(s, block, index); break;
                case OptIsSome i: CheckOptIsSome(i, block, index); break;
                case OptGet g: CheckOptGet(g, block, index); break;
                case NewVariant v: CheckNewVariant(v, block, index); break;
                case EnumTag t: CheckEnumTag(t, block, index); break;
                case EnumAs a: CheckEnumAs(a, block, index); break;
                case MakeInterface m: CheckMakeInterface(m, block, index); break;
                case CallVirt c: CheckCallVirt(c, block, index); break;
                case StructCopy c: CheckStructCopy(c, block, index); break;
                case LoadGlobal l: CheckLoadGlobal(l, block, index); break;
                case StoreGlobal g: CheckStoreGlobal(g, block, index); break;
                case MakeClosure m: CheckMakeClosure(m, block, index); break;
                case CallIndirect c: CheckCallIndirect(c, block, index); break;
                default:
                    throw new InternalCompilationException(
                        $"ir-verifier: unhandled op {op.GetType().Name}");
            }
        }

        private void CheckConst(Const c, BlockId block, int index)
        {
            RequireDestType(c.Dest, c.Type, "const", block, index);

            if (c.Type is not IrScalarType scalar)
            {
                Report(block, index, $"const type {Show(c.Type)} is not a scalar");
                return;
            }

            if (!ConstKindMatches(c.Value, scalar.Kind))
            {
                Report(block, index, $"{ConstKindName(c.Value)} const does not match type {Show(c.Type)}");
                return;
            }

            switch (c.Value)
            {
                // Kodierung von IntConst: Zweierkomplement, nullerweitert auf 64 Bit. Der Wert
                // muss als Bitmuster in die deklarierte Breite passen — fängt ein Lowering, das
                // ein Literal nicht getrunkiert/sign-extended hat.
                case IntConst ic when !FitsWidth(ic.Value, scalar.Kind):
                    Report(block, index,
                        $"integer const {N(ic.Value)} does not fit the bit pattern of {Show(c.Type)}");
                    break;

                // Ein f32-Const, dessen Wert kein f32-Wert ist, heißt: das Lowering hat nicht
                // verengt. Nicht-endliche Werte (NaN/Inf) sind in f32 darstellbar und ausgenommen.
                case FloatConst fc when scalar.Kind == IrScalar.F32
                                        && double.IsFinite(fc.Value)
                                        && (float)fc.Value != fc.Value:
                    Report(block, index,
                        $"float const {fc.Value.ToString("R", CultureInfo.InvariantCulture)} " +
                        "is not exactly representable as f32");
                    break;

                case CharConst ch when !IsUnicodeScalarValue(ch.CodePoint):
                    Report(block, index,
                        $"char const {N(ch.CodePoint)} is not a Unicode scalar value");
                    break;
            }
        }

        private void CheckBinOp(BinOp b, BlockId block, int index)
        {
            var lhs = TypeOf(b.Lhs);
            var rhs = TypeOf(b.Rhs);

            // Sprache.md §6.5: Numerik ist strikt, kein implizites Widening.
            if (!IrType.Equal(lhs, rhs))
            {
                Report(block, index,
                    $"operand types differ: {b.Lhs} is {Show(lhs)}, {b.Rhs} is {Show(rhs)}");
                return;
            }

            if (b.Kind.IsComparison())
            {
                // Vergleiche liefern bool. Der Operandentyp steht NICHT auf der Instruktion,
                // sondern in der Temp-Tabelle — der Emitter schlägt ihn dort nach, weil
                // signed/unsigned verschiedene Opcodes sind. Bewusst kein zweites Typ-Feld:
                // das wäre eine dritte Wahrheitsquelle, die driften kann.
                if (!IsBool(b.Type) || !IsBool(TypeOf(b.Dest)))
                    Report(block, index,
                        $"comparison must produce bool, found type {Show(b.Type)} " +
                        $"and dest {b.Dest} of {Show(TypeOf(b.Dest))}");

                var ordering = b.Kind is not (IrBinKind.Eq or IrBinKind.Ne);
                if (ordering && !IsNumeric(lhs))
                    Report(block, index,
                        $"ordering comparison {IrNames.Bin(b.Kind)} on non-numeric type {Show(lhs)}");
                else if (!ordering && !IsEquatable(lhs))
                    Report(block, index, $"equality comparison on type {Show(lhs)}");

                return;
            }

            if (!IrType.Equal(b.Type, lhs) || !IrType.Equal(TypeOf(b.Dest), lhs))
                Report(block, index,
                    $"{IrNames.Bin(b.Kind)} result must have the operand type {Show(lhs)}, found type " +
                    $"{Show(b.Type)} and dest {b.Dest} of {Show(TypeOf(b.Dest))}");

            if (IsBitwiseOrShift(b.Kind))
            {
                if (!IsInteger(lhs))
                    Report(block, index, $"{IrNames.Bin(b.Kind)} on non-integer type {Show(lhs)}");
            }
            else if (!IsNumeric(lhs))
            {
                // string+string / T[]+T[] (Sprache.md §6.5) sind eingebaute Semantik, aber KEIN
                // BinOp: sie lowern zu einem Call/Intrinsic. Sonst würde der add-Opcode polymorph
                // und müsste zur Laufzeit Typ-Dispatch machen — gegen ADR-013.
                var hint = IsStringLike(lhs) && b.Kind is IrBinKind.Add or IrBinKind.Mul
                    ? " (string concatenation/repetition lowers to a call, not a binop)"
                    : "";
                Report(block, index, $"{IrNames.Bin(b.Kind)} on non-numeric type {Show(lhs)}{hint}");
            }
        }

        private void CheckUnOp(UnOp u, BlockId block, int index)
        {
            var operand = TypeOf(u.Operand);

            if (!IrType.Equal(u.Type, operand) || !IrType.Equal(TypeOf(u.Dest), operand))
                Report(block, index,
                    $"{IrNames.Un(u.Kind)} result must have the operand type {Show(operand)}, found type " +
                    $"{Show(u.Type)} and dest {u.Dest} of {Show(TypeOf(u.Dest))}");

            switch (u.Kind)
            {
                case IrUnKind.Neg when !IsNumeric(operand):
                    Report(block, index, $"neg on non-numeric type {Show(operand)}");
                    break;
                case IrUnKind.Not when !IsBool(operand):
                    Report(block, index, $"not on non-bool type {Show(operand)}");
                    break;
                case IrUnKind.BitNot when !IsInteger(operand):
                    Report(block, index, $"bitnot on non-integer type {Show(operand)}");
                    break;
            }
        }

        private void CheckConvert(Convert cv, BlockId block, int index)
        {
            var operand = TypeOf(cv.Operand);
            if (!IrType.Equal(cv.From, operand))
                Report(block, index,
                    $"convert declares from-type {Show(cv.From)} but {cv.Operand} is {Show(operand)}");

            RequireDestType(cv.Dest, cv.To, "convert", block, index);

            // Sprache.md §6.5: 'as' konvertiert in v1 nur Numerik <-> Numerik.
            if (!IsNumeric(cv.From) || !IsNumeric(cv.To))
            {
                Report(block, index,
                    $"convert {Show(cv.From)} -> {Show(cv.To)} is not numeric<->numeric");
                return;
            }

            // Das Lowering elidiert Identitäts-Konvertierungen (`x as int` bei x: int ist legales
            // Lyric, ergibt aber keinen sinnvollen Opcode).
            if (IrType.Equal(cv.From, cv.To))
                Report(block, index, $"identity convert {Show(cv.From)} -> {Show(cv.To)}");
        }

        private void CheckLoadLocal(LoadLocal l, BlockId block, int index)
        {
            if (LocalTypeOf(l.Local) is not { } localType)
            {
                Report(block, index, $"load from unknown local {l.Local}");
                return;
            }

            if (!IrType.Equal(l.Type, localType))
                Report(block, index,
                    $"load declares type {Show(l.Type)} but {l.Local} is {Show(localType)}");

            RequireDestType(l.Dest, l.Type, "load", block, index);
        }

        private void CheckStoreLocal(StoreLocal s, BlockId block, int index)
        {
            if (LocalTypeOf(s.Local) is not { } localType)
            {
                Report(block, index, $"store to unknown local {s.Local}");
                return;
            }

            var value = TypeOf(s.Value);
            if (!IrType.Equal(localType, value))
                Report(block, index,
                    $"store of {s.Value} ({Show(value)}) into {s.Local} ({Show(localType)})");
        }

        private void CheckCall(Call k, BlockId block, int index)
        {
            if (k.Target.Value < 0 || k.Target.Value >= _module.Functions.Count)
            {
                Report(block, index, $"call target {k.Target} is out of range " +
                                     $"(module has {N(_module.Functions.Count)} function(s))");
                return;
            }

            var callee = _module.Functions[k.Target.Value];

            if (k.Args.Length != callee.ParamCount)
            {
                Report(block, index, $"call to {callee.Name} passes {N(k.Args.Length)} arg(s), " +
                                     $"expected {N(callee.ParamCount)}");
            }
            else if (callee.ParamCount > callee.Locals.Count)
            {
                // Der Callee ist selbst malformed; das wird bei SEINER Prüfung gemeldet. Hier
                // nur nicht daneben greifen.
                Report(block, index,
                    $"cannot check args: callee {callee.Name} has a malformed local table");
            }
            else
            {
                for (var i = 0; i < k.Args.Length; i++)
                {
                    var expected = callee.Locals[i].Type; // Konvention: erste N Locals = Params
                    var actual = TypeOf(k.Args[i]);
                    if (!IrType.Equal(expected, actual))
                        Report(block, index, $"call to {callee.Name}: arg {N(i)} is {Show(actual)}, " +
                                             $"expected {Show(expected)}");
                }
            }

            var returnsVoid = IsVoid(callee.ReturnType);
            if (returnsVoid && k.Dest is { } unwanted)
                Report(block, index,
                    $"call to void function {callee.Name} must not have a dest (found {unwanted})");
            else if (!returnsVoid && k.Dest is null)
                Report(block, index,
                    $"call to {callee.Name} returning {Show(callee.ReturnType)} must have a dest");
            else if (k.Dest is { } dest && !IrType.Equal(callee.ReturnType, TypeOf(dest)))
                Report(block, index, $"call dest {dest} is {Show(TypeOf(dest))} but {callee.Name} " +
                                     $"returns {Show(callee.ReturnType)}");
        }

        /// <summary>Wie <see cref="CheckCall"/>, nur gegen die Import-Tabelle. Ein Import hat keinen
    /// Rumpf, also kommt die Signatur aus seiner Deklaration statt aus einer Funktion.</summary>
    private void CheckCallImport(CallImport k, BlockId block, int index)
    {
        if (k.Target.Value < 0 || k.Target.Value >= _module.Imports.Count)
        {
            Report(block, index, $"import target {k.Target} is out of range " +
                                 $"(module has {N(_module.Imports.Count)} import(s))");
            return;
        }

        var import = _module.Imports[k.Target.Value];

        if (k.Args.Length != import.ParamTypes.Length)
        {
            Report(block, index, $"call to import '{import.Name}' passes {N(k.Args.Length)} arg(s), " +
                                 $"expected {N(import.ParamTypes.Length)}");
        }
        else
        {
            for (var i = 0; i < k.Args.Length; i++)
            {
                var actual = TypeOf(k.Args[i]);
                if (!IrType.Equal(import.ParamTypes[i], actual))
                    Report(block, index, $"call to import '{import.Name}': arg {N(i)} is " +
                                         $"{Show(actual)}, expected {Show(import.ParamTypes[i])}");
            }
        }

        var returnsVoid = IsVoid(import.ReturnType);
        if (returnsVoid && k.Dest is { } unwanted)
            Report(block, index,
                $"call to void import '{import.Name}' must not have a dest (found {unwanted})");
        else if (!returnsVoid && k.Dest is null)
            Report(block, index,
                $"call to import '{import.Name}' returning {Show(import.ReturnType)} must have a dest");
        else if (k.Dest is { } dest && !IrType.Equal(import.ReturnType, TypeOf(dest)))
            Report(block, index, $"call dest {dest} is {Show(TypeOf(dest))} but import " +
                                 $"'{import.Name}' returns {Show(import.ReturnType)}");
    }

    /// <summary>Löst eine <see cref="TypeId"/> gegen die Modul-Tabelle auf. <c>null</c> heißt: der
    /// Index zeigt ins Leere und wurde bereits gemeldet — der Aufrufer bricht dann ab, statt mit
    /// einem Ersatz-Layout weiterzuprüfen und Folgebefunde zu erzeugen.</summary>
    /// <summary>
    /// <c>mkiface</c>: die Quelle ist eine Klassen- oder Enum-Referenz, das Ziel ein
    /// Interface-Eintrag, und es <b>gibt eine vtable-Zeile fuer genau dieses Paar</b>.
    ///
    /// <para>Die letzte Bedingung ist die eigentliche Invariante. Ohne sie entstuende ein
    /// Interface-Wert, dessen konkreter Typ das Interface gar nicht erfuellt — und der Dispatch
    /// liefe erst beim Aufruf ins Leere, mit einem Fehler, der nichts mehr ueber die Ursache sagt.
    /// Dieselbe Rolle, die bei Enums „eine Variante gehoert zu genau einem Enum" spielt.</para>
    /// </summary>
    private void CheckMakeInterface(MakeInterface m, BlockId block, int index)
    {
        var concrete = TypeOf(m.Value) switch
        {
            IrRefType r => (TypeId?)r.Type,
            IrStructType v => v.Type,
            IrEnumType e => e.Type,
            _ => null,
        };

        if (concrete is null)
        {
            Report(block, index,
                $"mkiface expects a class, struct or enum value, found {Show(TypeOf(m.Value))}");
            return;
        }

        if (concrete.Value != m.Concrete)
        {
            Report(block, index, $"mkiface declares concrete type {m.Concrete} but its operand is " +
                                 $"{Show(TypeOf(m.Value))}");
            return;
        }

        if (ResolveType(m.Concrete, "mkiface", block, index) is null) return;
        if (ResolveType(m.Interface, "mkiface", block, index) is not { } iface) return;

        if (!iface.IsInterface)
        {
            Report(block, index, $"mkiface targets type {m.Interface} ({iface.Name}), " +
                                 "which is not an interface");
            return;
        }

        if (!_module.Impls.Any(i => i.Type == m.Concrete && i.Interface == m.Interface))
        {
            Report(block, index, $"mkiface lifts type {m.Concrete} to interface {m.Interface} " +
                                 $"({iface.Name}), but no impl row says it implements it");
            return;
        }

        RequireDestType(m.Dest, new IrInterfaceType(m.Interface), "mkiface", block, index);
    }

    /// <summary>
    /// <c>callvirt</c>: der Empfaenger (Arg 0) ist ein Wert genau dieses Interfaces, und der Slot
    /// liegt in dessen Methodenliste.
    ///
    /// <para>Die Argumenttypen werden <b>nicht</b> gegen eine Zielfunktion geprueft — es gibt
    /// keine: welche laeuft, entscheidet die Laufzeit. Die Signatur steht am Interface, und dass
    /// alle Implementierungen sie erfuellen, hat die Sema geprueft (Konformanz). Was der Verifier
    /// hier haelt, ist die Form; die Kongruenz der vtable-Zeilen prueft
    /// <see cref="CheckImpls"/>.</para>
    /// </summary>
    /// <summary>
    /// <c>mkclosure</c> (ADR-018): der Fat Pointer aus Zielfunktion und Environment.
    ///
    /// <para>Geprueft wird, dass das Ziel existiert, dass der Zieltyp ein Funktionstyp ist und
    /// dass die <b>Aritaet</b> stimmt — die gehobene Funktion hat einen Parameter mehr als der
    /// Typ, naemlich das Environment auf Position 0. Ein Fehler hier waere zur Laufzeit ein Frame
    /// mit falscher Slot-Zahl, also ein Falsch-Slot-Read statt eines Absturzes.</para>
    /// </summary>
    private void CheckMakeClosure(MakeClosure m, BlockId block, int index)
    {
        RequireDestType(m.Dest, m.Type, "mkclosure", block, index);

        if (m.Target.Value < 0 || m.Target.Value >= _module.Functions.Count)
        {
            Report(block, index, $"mkclosure targets {m.Target}, which is outside " +
                                 $"{N(_module.Functions.Count)} function(s)");
            return;
        }

        var target = _module.Functions[m.Target.Value];
        var expected = m.Type.Parameters.Length + (m.Environment is null ? 0 : 1);
        if (target.ParamCount != expected)
            Report(block, index,
                $"mkclosure targets {target.Name}, which takes {N(target.ParamCount)} " +
                $"parameter(s), but the closure type needs {N(expected)} " +
                (m.Environment is null ? "(no environment)" : "(including the environment)"));

        // Ein Environment ist ein gewoehnliches Objekt — genau deshalb braucht es hier keinen
        // Sonderfall im Typsystem.
        if (m.Environment is { } env && TypeOf(env) is not IrRefType)
            Report(block, index, $"mkclosure environment is {Show(TypeOf(env))}, expected a reference");
    }

    /// <summary>
    /// <c>callind</c> (ADR-018): Aufruf ueber einen Funktionswert.
    ///
    /// <para>Die Signatur steht im TYP des Aufgerufenen, nicht in einer Deklaration — das ist der
    /// ganze Unterschied zu <c>call</c>. Geprueft werden Aritaet, Parametertypen und der
    /// Rueckgabetyp gegen genau diesen Typ.</para>
    /// </summary>
    private void CheckCallIndirect(CallIndirect c, BlockId block, int index)
    {
        if (TypeOf(c.Callee) is not IrFunctionType signature)
        {
            Report(block, index, $"callind callee is {Show(TypeOf(c.Callee))}, expected a function value");
            return;
        }

        if (c.Args.Length != signature.Parameters.Length)
        {
            Report(block, index, $"callind passes {N(c.Args.Length)} arg(s), " +
                                 $"but {Show(signature)} takes {N(signature.Parameters.Length)}");
            return;
        }

        for (var i = 0; i < c.Args.Length; i++)
            if (!IrType.Equal(TypeOf(c.Args[i]), signature.Parameters[i]))
                Report(block, index, $"callind argument {N(i)} is {Show(TypeOf(c.Args[i]))}, " +
                                     $"expected {Show(signature.Parameters[i])}");

        if (!IrType.Equal(c.ReturnType, signature.Return))
            Report(block, index, $"callind is annotated {Show(c.ReturnType)}, " +
                                 $"but {Show(signature)} returns {Show(signature.Return)}");

        if (c.Dest is { } dest) RequireDestType(dest, signature.Return, "callind", block, index);
    }

    private void CheckCallVirt(CallVirt c, BlockId block, int index)
    {
        if (c.Args.Length == 0)
        {
            Report(block, index, "callvirt has no receiver (argument 0 is the interface value)");
            return;
        }

        if (ResolveType(c.Interface, "callvirt", block, index) is not { } iface) return;

        if (!iface.IsInterface)
        {
            Report(block, index, $"callvirt targets type {c.Interface} ({iface.Name}), " +
                                 "which is not an interface");
            return;
        }

        if (c.Slot < 0 || c.Slot >= iface.MethodSlots.Length)
        {
            Report(block, index, $"callvirt slot {N(c.Slot)} is out of range for interface " +
                                 $"{c.Interface} ({iface.Name} has {N(iface.MethodSlots.Length)} slot(s))");
            return;
        }

        if (TypeOf(c.Args[0]) is not IrInterfaceType receiver || receiver.Type != c.Interface)
        {
            Report(block, index, $"callvirt receiver is {Show(TypeOf(c.Args[0]))}, " +
                                 $"expected interface {c.Interface}");
            return;
        }

        if (c.Dest is { } dest) RequireDestType(dest, c.ReturnType, "callvirt", block, index);
    }

    /// <summary>
    /// <c>structcopy</c>: Quelle und Ziel sind derselbe Wert-Typ, und der Eintrag ist wirklich
    /// einer.
    ///
    /// <para>Ein <c>structcopy</c> auf einer Klasse waere kein Fehler, den die Laufzeit bemerkt —
    /// sie kopierte einfach ein Slot-Array, das eigentlich geteilt gehoert. Ein stiller
    /// Semantikbruch also, und genau deshalb steht die Pruefung hier.</para>
    /// </summary>
    private void CheckStructCopy(StructCopy c, BlockId block, int index)
    {
        if (ResolveType(c.Type, "structcopy", block, index) is not { } layout) return;

        if (!layout.IsStruct)
        {
            Report(block, index, $"structcopy targets type {c.Type} ({layout.Name}), which is a " +
                                 "reference type — copying it would break sharing");
            return;
        }

        if (TypeOf(c.Value) is not IrStructType source || source.Type != c.Type)
        {
            Report(block, index, $"structcopy declares type {c.Type} but its operand is " +
                                 $"{Show(TypeOf(c.Value))}");
            return;
        }

        RequireDestType(c.Dest, new IrStructType(c.Type), "structcopy", block, index);
    }

    private void CheckLoadGlobal(LoadGlobal l, BlockId block, int index)
    {
        if (ResolveGlobal(l.Global, "ldglobal", block, index) is not { } global) return;

        if (!IrType.Equal(l.Type, global.Type))
            Report(block, index, $"ldglobal of {l.Global} is declared {Show(global.Type)} " +
                                 $"but the instruction says {Show(l.Type)}");
        else
            RequireDestType(l.Dest, global.Type, "ldglobal", block, index);
    }

    private void CheckStoreGlobal(StoreGlobal g, BlockId block, int index)
    {
        if (ResolveGlobal(g.Global, "stglobal", block, index) is not { } global) return;

        var actual = TypeOf(g.Value);
        if (!IrType.Equal(global.Type, actual))
            Report(block, index, $"stglobal into {g.Global} takes {Show(global.Type)}, " +
                                 $"but {g.Value} is {Show(actual)}");
    }

    private IrGlobal? ResolveGlobal(GlobalId id, string what, BlockId block, int index)
    {
        if (id.Value >= 0 && id.Value < _module.Globals.Count) return _module.Globals[id.Value];

        Report(block, index, $"{what} references global {id} which is out of range " +
                             $"(module has {N(_module.Globals.Count)} global(s))");
        return null;
    }

    private IrTypeDef? ResolveType(TypeId type, string what, BlockId block, int index)
    {
        if (type.Value >= 0 && type.Value < _module.Types.Count) return _module.Types[type.Value];

        Report(block, index, $"{what} references type {type} which is out of range " +
                             $"(module has {N(_module.Types.Count)} type(s))");
        return null;
    }

    /// <summary>Liefert den deklarierten Feldtyp, oder <c>null</c> bei Bereichs-/Layout-Fehler.
    /// Prüft dabei auch, dass Typen- und Namensliste gleich lang sind: läuft das auseinander,
    /// fiele es sonst erst im Printer als Index-Ausnahme auf.</summary>
    private IrType? ResolveField(IrTypeDef def, TypeId type, FieldId field, string what,
        BlockId block, int index)
    {
        if (def.FieldTypes.Length != def.FieldNames.Length)
        {
            Report(block, index, $"type {type} '{def.Name}' has {N(def.FieldTypes.Length)} field " +
                                 $"type(s) but {N(def.FieldNames.Length)} name(s)");
            return null;
        }

        if (field.Value >= 0 && field.Value < def.FieldTypes.Length) return def.FieldTypes[field.Value];

        Report(block, index, $"{what} references field {field} of type {type} '{def.Name}', " +
                             $"which has {N(def.FieldTypes.Length)} field(s)");
        return null;
    }

    /// <summary>Der Objekt-Operand muss eine Referenz auf <b>genau</b> den Typ sein, den die
    /// Instruktion nennt. Beides zu tragen ist Absicht (Bytecode.md §5): der Typ im
    /// Instruktionsstrom macht die Feldindex-Prüfung beim Laden ohne Datenfluss-Analyse möglich.
    /// Genau deshalb muss der Verifier hier durchsetzen, dass die beiden nicht auseinanderlaufen —
    /// sonst prüft der Bytecode-Leser später gegen das falsche Layout.</summary>
    /// <summary>
    /// Der Operand haelt ein Objekt dieses Layouts — eine Klasse <b>oder</b> ein struct.
    ///
    /// <para>Beide sind zur Laufzeit dasselbe Slot-Array; der Feldzugriff ist derselbe
    /// Array-Zugriff. Der Unterschied zwischen Wert- und Referenz-Semantik steckt nicht im
    /// Zugriff, sondern in den Bindepunkten (<c>structcopy</c>) — <c>ldfld</c> und <c>stfld</c>
    /// duerfen deshalb beides akzeptieren.</para>
    /// </summary>
    private bool RequireObject(TempId obj, TypeId type, string what, BlockId block, int index)
    {
        var actual = TypeOf(obj);
        if (actual is IrRefType r && r.Type == type) return true;
        if (actual is IrStructType v && v.Type == type) return true;

        Report(block, index, $"{what} expects {obj} to hold type {type}, found {Show(actual)}");
        return false;
    }

    /// <summary>Wie ein Layout an einem Wert aussieht: als Referenz oder als Wert-Typ. Die
    /// Types-Tabelle entscheidet, nicht der Aufrufer — zwei Meinungen darueber waeren ein
    /// <c>structcopy</c> auf einer Klasse oder eine geteilte Struct-Instanz.</summary>
    private IrType LayoutTypeOf(TypeId type) =>
        _module.Types[type.Value].IsStruct ? new IrStructType(type) : new IrRefType(type);

    private void CheckNewObject(NewObject n, BlockId block, int index)
    {
        if (ResolveType(n.Type, "newobj", block, index) is null) return;

        var expected = LayoutTypeOf(n.Type);
        if (!IrType.Equal(n.Result, expected))
            Report(block, index, $"newobj of {n.Type} yields {Show(expected)} " +
                                 $"but the instruction says {Show(n.Result)}");

        RequireDestType(n.Dest, expected, "newobj", block, index);
    }

    private void CheckLoadField(LoadField f, BlockId block, int index)
    {
        if (ResolveType(f.Type, "loadfield", block, index) is not { } def) return;
        if (ResolveField(def, f.Type, f.Field, "loadfield", block, index) is not { } declared) return;
        if (!RequireObject(f.Object, f.Type, "loadfield", block, index)) return;

        // Wie überall in dieser IR: das Type-Feld auf der Instruktion ist eine Kopie für den
        // Printer, die Temp-Tabelle ist die Autorität. Beide gegen die Deklaration zu prüfen ist
        // der Kern-Job des Verifiers.
        if (!IrType.Equal(f.FieldType, declared))
            Report(block, index, $"loadfield of {f.Type}{f.Field} is declared {Show(declared)} " +
                                 $"but the instruction says {Show(f.FieldType)}");
        else
            RequireDestType(f.Dest, declared, "loadfield", block, index);
    }

    private void CheckStoreField(StoreField f, BlockId block, int index)
    {
        if (ResolveType(f.Type, "storefield", block, index) is not { } def) return;
        if (ResolveField(def, f.Type, f.Field, "storefield", block, index) is not { } declared) return;
        if (!RequireObject(f.Object, f.Type, "storefield", block, index)) return;

        var actual = TypeOf(f.Value);
        if (!IrType.Equal(declared, actual))
            Report(block, index, $"storefield into {f.Type}{f.Field} takes {Show(declared)}, " +
                                 $"but {f.Value} is {Show(actual)}");
    }

    /// <summary>Der Array-Operand muss ein Array sein; liefert den Elementtyp, oder <c>null</c>
    /// nach gemeldetem Befund.</summary>
    private IrType? RequireArray(TempId array, string what, BlockId block, int index)
    {
        if (TypeOf(array) is IrArrayType a) return a.Element;

        Report(block, index, $"{what} expects {array} to be an array, found {Show(TypeOf(array))}");
        return null;
    }

    /// <summary>Ein Index muss <c>i64</c> sein. Nicht <b>ob</b> er in Grenzen liegt — das ist ein
    /// Laufzeitwert und wird zur Laufzeit zum <c>panic</c> (Sprache.md §9). Der Verifier prüft die
    /// Form, nicht das Programm.</summary>
    private void RequireIndex(TempId index, string what, BlockId block, int at)
    {
        if (TypeOf(index) is IrScalarType { Kind: IrScalar.I64 }) return;

        Report(block, at, $"{what} index {index} is {Show(TypeOf(index))}, expected i64");
    }

    private void CheckNewArray(NewArray a, BlockId block, int index)
    {
        RequireDestType(a.Dest, new IrArrayType(a.Element), "newarr", block, index);

        for (var i = 0; i < a.Elements.Length; i++)
        {
            var actual = TypeOf(a.Elements[i]);
            if (!IrType.Equal(a.Element, actual))
                Report(block, index, $"newarr element {N(i)} is {Show(actual)}, " +
                                     $"expected {Show(a.Element)}");
        }
    }

    private void CheckLoadElem(LoadElem e, BlockId block, int index)
    {
        if (RequireArray(e.Array, "loadelem", block, index) is not { } element) return;
        RequireIndex(e.Index, "loadelem", block, index);

        // Wie überall: das Type-Feld an der Instruktion ist eine Kopie für den Printer, die
        // Temp-Tabelle ist die Autorität.
        if (!IrType.Equal(e.Element, element))
            Report(block, index, $"loadelem yields {Show(element)} but the instruction says " +
                                 $"{Show(e.Element)}");
        else
            RequireDestType(e.Dest, element, "loadelem", block, index);
    }

    private void CheckStoreElem(StoreElem e, BlockId block, int index)
    {
        if (RequireArray(e.Array, "storeelem", block, index) is not { } element) return;
        RequireIndex(e.Index, "storeelem", block, index);

        var actual = TypeOf(e.Value);
        if (!IrType.Equal(element, actual))
            Report(block, index, $"storeelem takes {Show(element)}, but {e.Value} is {Show(actual)}");
    }

    private void CheckArrayLen(ArrayLen a, BlockId block, int index)
    {
        if (RequireArray(a.Array, "arraylen", block, index) is null) return;
        RequireDestType(a.Dest, new IrScalarType(IrScalar.I64), "arraylen", block, index);
    }

    private void CheckArrayConcat(ArrayConcat c, BlockId block, int index)
    {
        if (RequireArray(c.Left, "arrcat", block, index) is not { } left) return;
        if (RequireArray(c.Right, "arrcat", block, index) is not { } right) return;

        if (!IrType.Equal(left, right))
        {
            Report(block, index, $"arrcat joins {Show(left)}[] and {Show(right)}[]");
            return;
        }

        if (!IrType.Equal(c.Element, left))
            Report(block, index, $"arrcat yields {Show(left)}[] but the instruction says " +
                                 $"{Show(c.Element)}[]");
        else
            RequireDestType(c.Dest, new IrArrayType(left), "arrcat", block, index);
    }

    private void CheckArrayRepeat(ArrayRepeat r, BlockId block, int index)
    {
        if (RequireArray(r.Array, "arrrep", block, index) is not { } element) return;
        RequireIndex(r.Count, "arrrep", block, index);

        if (!IrType.Equal(r.Element, element))
            Report(block, index, $"arrrep yields {Show(element)}[] but the instruction says " +
                                 $"{Show(r.Element)}[]");
        else
            RequireDestType(r.Dest, new IrArrayType(element), "arrrep", block, index);
    }

    /// <summary>Ein Optional ist nicht schachtelbar (Bytecode.md §5): <c>??T</c> wäre in der
    /// Laufzeit-Darstellung nicht von <c>?T</c> unterscheidbar.</summary>
    private bool RequireNotOptional(IrType inner, string what, BlockId block, int index)
    {
        if (inner is not IrOptionalType) return true;

        Report(block, index, $"{what} of {Show(inner)} — optionals do not nest");
        return false;
    }

    private void CheckOptNone(OptNone n, BlockId block, int index)
    {
        if (!RequireNotOptional(n.Inner, "optnone", block, index)) return;
        RequireDestType(n.Dest, new IrOptionalType(n.Inner), "optnone", block, index);
    }

    private void CheckOptSome(OptSome s, BlockId block, int index)
    {
        if (!RequireNotOptional(s.Inner, "optsome", block, index)) return;

        var actual = TypeOf(s.Value);
        if (!IrType.Equal(s.Inner, actual))
            Report(block, index, $"optsome wraps {Show(actual)} but the instruction says {Show(s.Inner)}");
        else
            RequireDestType(s.Dest, new IrOptionalType(s.Inner), "optsome", block, index);
    }

    private void CheckOptIsSome(OptIsSome i, BlockId block, int index)
    {
        if (TypeOf(i.Option) is not IrOptionalType)
            Report(block, index, $"optissome expects an optional, found {Show(TypeOf(i.Option))}");
        else
            RequireDestType(i.Dest, new IrScalarType(IrScalar.Bool), "optissome", block, index);
    }

    private void CheckOptGet(OptGet g, BlockId block, int index)
    {
        if (TypeOf(g.Option) is not IrOptionalType option)
        {
            Report(block, index, $"optget expects an optional, found {Show(TypeOf(g.Option))}");
            return;
        }

        if (!IrType.Equal(g.Inner, option.Inner))
            Report(block, index, $"optget yields {Show(option.Inner)} but the instruction says " +
                                 $"{Show(g.Inner)}");
        else
            RequireDestType(g.Dest, option.Inner, "optget", block, index);
    }

    /// <summary>
    /// Eine Variante gehört zu genau einem Enum. Das zu prüfen ist der Kern der Enum-Invarianten:
    /// <c>enumas</c> auf eine fremde Variante wäre ein Feldzugriff mit dem falschen Layout, und die
    /// Load-Zeit-Validierung könnte ihn nicht abfangen — sie sieht nur, dass beide Indizes gültig
    /// sind.
    /// </summary>
    private int VariantIndexIn(TypeId enumType, TypeId variant)
    {
        if (enumType.Value < 0 || enumType.Value >= _module.Types.Count) return -1;
        return Array.IndexOf(_module.Types[enumType.Value].Variants, variant);
    }

    private void CheckNewVariant(NewVariant v, BlockId block, int index)
    {
        if (ResolveType(v.Enum, "newvariant", block, index) is not { } enumDef) return;
        if (ResolveType(v.Variant, "newvariant", block, index) is not { } layout) return;

        if (!enumDef.IsEnum)
        {
            Report(block, index, $"newvariant names type {v.Enum} '{enumDef.Name}', which is not an enum");
            return;
        }

        if (VariantIndexIn(v.Enum, v.Variant) < 0)
        {
            Report(block, index, $"variant {v.Variant} '{layout.Name}' does not belong to enum " +
                                 $"{v.Enum} '{enumDef.Name}'");
            return;
        }

        // Slot 0 ist das Tag und wird von der Instruktion selbst gesetzt — die Argumente sind die
        // Nutzfelder ab Slot 1.
        var payload = layout.FieldTypes.Length - 1;
        if (v.Fields.Length != payload)
        {
            Report(block, index, $"newvariant {v.Variant} '{layout.Name}' takes {N(payload)} " +
                                 $"field(s), got {N(v.Fields.Length)}");
            return;
        }

        for (var i = 0; i < v.Fields.Length; i++)
        {
            var actual = TypeOf(v.Fields[i]);
            if (!IrType.Equal(layout.FieldTypes[i + 1], actual))
                Report(block, index, $"newvariant field {N(i)} is {Show(actual)}, " +
                                     $"expected {Show(layout.FieldTypes[i + 1])}");
        }

        RequireDestType(v.Dest, new IrEnumType(v.Enum), "newvariant", block, index);
    }

    private void CheckEnumTag(EnumTag t, BlockId block, int index)
    {
        if (TypeOf(t.Value) is not IrEnumType)
            Report(block, index, $"enumtag expects an enum, found {Show(TypeOf(t.Value))}");
        else
            RequireDestType(t.Dest, new IrScalarType(IrScalar.I64), "enumtag", block, index);
    }

    private void CheckEnumAs(EnumAs a, BlockId block, int index)
    {
        if (TypeOf(a.Value) is not IrEnumType source)
        {
            Report(block, index, $"enumas expects an enum, found {Show(TypeOf(a.Value))}");
            return;
        }

        if (ResolveType(a.Variant, "enumas", block, index) is null) return;

        if (VariantIndexIn(source.Type, a.Variant) < 0)
        {
            Report(block, index, $"enumas narrows to variant {a.Variant}, which does not belong to " +
                                 $"enum {source.Type}");
            return;
        }

        RequireDestType(a.Dest, new IrRefType(a.Variant), "enumas", block, index);
    }

    private void CheckTerminatorTypes(IrTerminator terminator, BlockId block)
        {
            switch (terminator)
            {
                case Return r:
                {
                    var returnsVoid = IsVoid(_fn.ReturnType);
                    if (returnsVoid && r.Value is { } unwanted)
                        ReportTerm(block, $"void function returns a value ({unwanted})");
                    else if (!returnsVoid && r.Value is null)
                        ReportTerm(block, $"function returns {Show(_fn.ReturnType)} " +
                                          "but 'ret' carries no value");
                    else if (r.Value is { } value && !IrType.Equal(_fn.ReturnType, TypeOf(value)))
                        ReportTerm(block, $"returns {value} ({Show(TypeOf(value))}), " +
                                          $"expected {Show(_fn.ReturnType)}");
                    break;
                }

                case CondBranch c:
                    if (!IsBool(TypeOf(c.Cond)))
                        ReportTerm(block, $"condition {c.Cond} is {Show(TypeOf(c.Cond))}, must be bool");
                    break;

                // Nur Throwable-Typen sind werfbar (Sprache.md §9) — geprueft hat das die Sema.
                // Hier bleibt die Form: ein Wert, der ueberhaupt ein Objekt ist. Ein Skalar zu
                // werfen waere ein Lowering-Bug, kein User-Fehler.
                case Throw t when TypeOf(t.Value) is not (IrRefType or IrInterfaceType):
                    ReportTerm(block, $"throws {t.Value} ({Show(TypeOf(t.Value))}); " +
                                      "only class and interface values are throwable");
                    break;

                case Throw:
                case EndFinally:
                case Branch:
                case Unreachable:
                    break; // keine Typ-Bedingungen

                default:
                    throw new InternalCompilationException(
                        $"ir-verifier: unhandled terminator {terminator.GetType().Name}");
            }
        }

        private void RequireDestType(TempId dest, IrType declared, string what, BlockId block, int index)
        {
            // Die Type-Felder auf den Instruktionen sind Kopien für den Printer; die Temp-Tabelle
            // ist die Autorität. Dass beide übereinstimmen, ist der Kern-Job des Verifiers.
            var fromTable = TypeOf(dest);
            if (!IrType.Equal(declared, fromTable))
                Report(block, index, $"{what} declares type {Show(declared)} but {dest} is " +
                                     $"{Show(fromTable)} in the temp table");
        }

        // ------------------------------------------------------------------ Tabellen-Lookups

        private bool IsKnownTemp(TempId temp) => temp.Value >= 0 && temp.Value < _fn.Temps.Count;

        /// <summary>Der Typ eines Temps kommt aus der Temp-Tabelle — sie ist die Autorität.
        /// Nur für Temps aufrufen, die <see cref="IsKnownTemp"/> passiert haben.</summary>
        private IrType TypeOf(TempId temp) => _fn.Temps[temp.Value].Type;

        /// <summary>null, wenn die Local-Id außerhalb der Tabelle liegt.</summary>
        private IrType? LocalTypeOf(LocalId local) =>
            local.Value >= 0 && local.Value < _fn.Locals.Count ? _fn.Locals[local.Value].Type : null;

        // ------------------------------------------------------------------ Typ-Prädikate

        // "Ist der Typ genau dieser Skalar?" läuft über Pattern-Matching, nicht über
        // IrType.Equal: die Frage ist für jeden Typ beantwortbar, und in CheckTables muss sie
        // auch für einen künftigen nicht-skalaren Typ eine Antwort geben statt zu werfen.
        // IrType.Equal ist für das andere Problem da — zwei Typen gegeneinander zu vergleichen.
        private static bool IsVoid(IrType type) => type is IrScalarType { Kind: IrScalar.Void };

        private static bool IsBool(IrType type) => type is IrScalarType { Kind: IrScalar.Bool };

        private static bool IsInteger(IrType type) => type is IrScalarType
        {
            Kind: IrScalar.I8 or IrScalar.I16 or IrScalar.I32 or IrScalar.I64
            or IrScalar.U8 or IrScalar.U16 or IrScalar.U32 or IrScalar.U64
        };

        private static bool IsFloat(IrType type) =>
            type is IrScalarType { Kind: IrScalar.F32 or IrScalar.F64 };

        private static bool IsNumeric(IrType type) => IsInteger(type) || IsFloat(type);

        private static bool IsStringLike(IrType type) =>
            type is IrScalarType { Kind: IrScalar.String };

        /// <summary>Was <c>eq</c>/<c>ne</c> vergleichen darf. Ordnungsvergleiche verlangen
        /// dagegen Numerik (Sprache.md §6.5) — char und string haben in v1 keine Ordnung.</summary>
        private static bool IsEquatable(IrType type) =>
            IsNumeric(type) || type is IrScalarType
            {
                Kind: IrScalar.Bool or IrScalar.Char or IrScalar.String
            };

        private static bool IsBitwiseOrShift(IrBinKind kind) => kind is
            IrBinKind.Shl or IrBinKind.Shr or
            IrBinKind.BitAnd or IrBinKind.BitOr or IrBinKind.BitXor;

        /// <summary>IntConst ist zweierkomplement-kodiert und auf 64 Bit nullerweitert; geprüft
        /// wird deshalb das Bitmuster, nicht der vorzeichenbehaftete Wertebereich.</summary>
        private static bool FitsWidth(ulong value, IrScalar kind) => kind switch
        {
            IrScalar.I8 or IrScalar.U8 => value <= byte.MaxValue,
            IrScalar.I16 or IrScalar.U16 => value <= ushort.MaxValue,
            IrScalar.I32 or IrScalar.U32 => value <= uint.MaxValue,
            IrScalar.I64 or IrScalar.U64 => true,
            _ => false
        };

        private static bool IsUnicodeScalarValue(int codePoint) =>
            codePoint >= 0 && codePoint <= 0x10FFFF && codePoint is < 0xD800 or > 0xDFFF;

        private static bool ConstKindMatches(IrConstValue value, IrScalar kind) => value switch
        {
            IntConst => IsInteger(new IrScalarType(kind)),
            FloatConst => IsFloat(new IrScalarType(kind)),
            BoolConst => kind == IrScalar.Bool,
            CharConst => kind == IrScalar.Char,
            StringConst => kind == IrScalar.String,
            _ => throw new InternalCompilationException(
                $"ir-verifier: unhandled const {value.GetType().Name}")
        };

        // ------------------------------------------------------------------ Befunde und Namen

        private void Report(string message) => _findings.Add($"{_fn.Name}: {message}");

        private void Report(BlockId block, int index, string message) =>
            Report($"{block}: #{N(index)}: {message}");

        private void ReportTerm(BlockId block, string message) =>
            Report($"{block}: terminator: {message}");

        private void ReportAt(BlockId block, int? index, string message)
        {
            if (index is { } i) Report(block, i, message);
            else ReportTerm(block, message);
        }

        // Invariant formatiert: Befunde werden in Tests per Substring verglichen, und eine Kultur
        // mit anderen Ziffernzeichen würde diese Assertions auf CI brechen.
        private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string N(ulong value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>Typ-Name für Fehlermeldungen, über <see cref="IrNames"/> also in derselben
        /// Schreibweise wie der Printer-Dump — man liest beide nebeneinander.</summary>
        /// <remarks>Der Fallback für nicht-skalare Typen ist Absicht: <c>Show</c> läuft
        /// ausschließlich beim Bauen von Befund-Texten, und ein Wurf würde dort genau den Befund
        /// verdecken, den wir gerade melden wollen. Der laute Wurf sitzt in
        /// <see cref="IrType.Equal"/>, wo er dem Vergleich selbst gilt.</remarks>
        private static string Show(IrType type) => type switch
        {
            IrScalarType s => IrNames.Scalar(s.Kind),
            IrRefType r => $"&{r.Type}",
            IrArrayType a => $"{Show(a.Element)}[]",
            IrOptionalType o => $"?{Show(o.Inner)}",
            IrEnumType e => $"enum {e.Type}",
        IrInterfaceType i => $"dyn {i.Type}",
        IrStructType v => $"val {v.Type}",
            _ => type.ToString() ?? type.GetType().Name
        };

        private static string ConstKindName(IrConstValue value) => value switch
        {
            IntConst => "integer",
            FloatConst => "float",
            BoolConst => "bool",
            CharConst => "char",
            StringConst => "string",
            _ => value.GetType().Name
        };
    }
}
