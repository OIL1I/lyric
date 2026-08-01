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

        foreach (var function in module.Functions)
        {
            // Namen sind die Symbol-Namen im Bytecode (ADR-013). Eine Kollision ist der
            // Kanarienvogel für die Monomorphisierung: zwei Instanzen, die auf denselben
            // gemangelten Namen fallen, wären ein stiller Falsch-Call.
            if (!seenNames.Add(function.Name))
                findings.Add($"{function.Name}: duplicate function name");

            new FunctionVerifier(module, function, findings).Run();
        }

        return findings;
    }

    /// <summary>Wie <see cref="Verify"/>, wirft aber bei Befunden. Für Aufrufstellen im Lowering,
    /// die von wohlgeformter IR ausgehen dürfen.</summary>
    /// <remarks>Bewusst <b>ohne</b> IR-Dump in der Nachricht: <see cref="IrPrinter"/> wirft selbst
    /// bei fehlendem Terminator und würde damit genau den Befund verdecken, den wir melden wollen.</remarks>
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
            if (!CheckCfgShape()) return;
            ComputeReachabilityAndAvailability();
            CheckInstructions();
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
