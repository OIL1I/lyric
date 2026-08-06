using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Einstieg ins Lowering: typgeprüfte Compilation → <see cref="IrModule"/>.
///
/// <para><b>Zwei Pässe.</b> Pass 1 vergibt jeder zu lowernden Funktion ihre
/// <see cref="FunctionId"/>, Pass 2 lowert die Bodies. Ohne die Trennung scheitert jeder
/// Vorwärts-Call und jede (wechselseitige) Rekursion, weil das Ziel beim Lowern des Calls noch
/// keine Id hätte. Dieselbe Lösung wie das 2-Pass-Deklarieren im Resolver — dasselbe Problem an
/// anderer Stelle.</para>
///
/// <para><b>Der Verifier läuft als Abnahme.</b> Ein Befund ist ein Bug in diesem Lowering, keine
/// User-Diagnose, deshalb wirft <see cref="IrVerifier.VerifyOrThrow"/>. In Tests und Debug-Builds
/// immer an; für Release-Builds kann der Aufrufer ihn abschalten (Vorbild: LLVMs Verifier in
/// Assert-Builds).</para>
///
/// <para><b>Was übersprungen wird</b>: bodylose Deklarationen (nichts zu lowern) und generische
/// Funktionen. Letztere brauchen die Worklist-Monomorphisierung — pro konkretem Typargument-Tupel
/// eine Instanz, ausgehend von den Wurzeln. Ein Call auf eine übersprungene Funktion findet keine
/// Id und meldet das als <c>LYR-IR0001</c> statt still falschen Code zu erzeugen.</para>
/// </summary>
public static class ModuleLowerer
{
    internal static readonly Dictionary<GenericParamSymbol, LyrType> NoSubstitution =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Läuft der Verifier, wenn der Aufrufer nichts anderes sagt? In Debug-Builds ja, im Release
    /// nein — Vorbild ist LLVMs Verifier in Assert-Builds.
    ///
    /// <para>Gemessen an 400 Funktionen / 18 400 Instruktionen: Lowering mit Verifikation 30 ms,
    /// ohne 2,8 ms. Die Prüfung ist also <b>90 % der Gesamtzeit</b>, nicht der vernachlässigbare
    /// Posten, nach dem sie aussieht — das meiste davon steckt im Availability-Dataflow, der pro
    /// Block HashSets alloziert und zum Fixpunkt iteriert.</para>
    ///
    /// <para>Das Risiko bleibt beherrschbar, weil der Bytecode-Leser beim Laden ohnehin
    /// vollständig validiert (<c>LYR-BC####</c>). Ein Lowering-Bug im Release-Compiler äußert sich
    /// also nicht als still falscher Code, sondern spätestens beim Laden — nur mit schlechterer
    /// Fehlermeldung als ein Verifier-Befund.</para>
    /// </summary>
    public static bool VerifyByDefault =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Lowert die Compilation. Liefert <c>null</c>, wenn Scope-Grenzen als
    /// <c>LYR-IR0001</c> gemeldet wurden — dann steht die Ursache in
    /// <paramref name="de"/>.</summary>
    /// <param name="verify"><c>null</c> = <see cref="VerifyByDefault"/>. Tests setzen den Wert
    /// explizit, damit ihr Ergebnis nicht von der Build-Konfiguration abhängt.</param>
    public static IrModule? Lower(Compilation compilation, BindingResult binding, TypeResult types,
        DiagnosticEngine de, bool? verify = null)
    {
        // Receiver == null: freie Funktion oder 'static fn'. Sonst der Typ, dessen Instanz als
        // Parameter 0 übergeben wird (ADR-014).
        var pending = new List<(FunctionDecl Decl, string Name, TypeSymbol? Receiver)>();
        var ids = new Dictionary<FunctionSymbol, FunctionId>(ReferenceEqualityComparer.Instance);
        var imports = new ImportTable();
        var typeTable = new TypeTable(binding);
        var globals = new GlobalTable();
        FunctionId? entry = null;
        var failed = false;

        // Pass 1 — Funktionstabelle. Die Reihenfolge ist Modul- dann Deklarations-Reihenfolge und
        // damit deterministisch: FunctionIds landen als Indizes im Bytecode (ADR-013).
        foreach (var module in compilation.Modules)
        {
            foreach (var decl in compilation.AstOf(module).Declarations)
            {
                if (decl is not FunctionDecl function) continue;
                if (function.Generics.Length > 0) continue;
                if (module.Members.LookupLocal(function.Name) is not FunctionSymbol symbol) continue;

                // Rumpflos in einem Stdlib-Modul = native Deklaration. Die Signatur steht in Lyric,
                // die Implementierung liegt im Host und wird beim Laden über den Namen gebunden.
                // In User-Code hat die Sema das schon als LYR-SEM0051 abgelehnt.
                if (function.Body is null)
                {
                    if (!compilation.IsNative(module)) continue;
                    imports.Declare(symbol, new IrImport(
                        NameMangling.ForFunction(module, function.Name),
                        function.Parameters.Select(p => DeclaredTypes.Lower(p.Type)).ToArray(),
                        DeclaredTypes.Lower(function.ReturnType)));
                    continue;
                }

                var id = new FunctionId(pending.Count);
                ids[symbol] = id;
                pending.Add((function, NameMangling.ForFunction(module, function.Name), null));

                // Entry-Contract (Sprache.md §11): genau ein 'main' pro Executable. Dass es
                // eindeutig ist, hat die Sema geprüft — hier wird es nur festgehalten.
                if (function.Name != "main") continue;

                if (function.Parameters.Length == 0) { entry = id; continue; }

                // §11 kennt auch 'fn main(args: string[])'. Das Lowering kann es nicht: die
                // Runtime müsste beim Start ein Array bauen und übergeben, und der Runner-Vertrag
                // (Bytecode.md §9) lehnt Programm-Argumente bis dahin ohnehin ab.
                //
                // Gemeldet statt übersprungen: bis 2026-08-06 fiel dieses main einfach durch die
                // Bedingung, das Modul bekam keine Start-Sektion, und der Compiler meldete
                // NICHTS. Ein Programm, das sauber übersetzt und dann als "Bibliothek" nicht
                // startet, ist die schlechteste aller Antworten — LYR-IR0001 heißt "noch nicht
                // gebaut", und genau das ist es.
                de.Report(LoweringDiagnostics.NotSupported, Severity.Error, function.Span,
                    "'fn main(args: string[])' is specified (Sprache.md §11) but not lowered yet; "
                    + "use a parameterless 'main' until program arguments arrive");
                failed = true;
            }

            // Methoden sind gewöhnliche Funktionen mit dem Empfänger als Parameter 0 — dieselbe
            // Konvention wie CIL. Der Unterschied zwischen Instanz- und static-Methode ist damit
            // allein die Parameterliste, und P3 muss für die vtable nur noch entscheiden, WELCHE
            // Funktion gerufen wird, nicht wie sie aussieht.
            foreach (var decl in compilation.AstOf(module).Declarations)
            {
                // Klassen und Enums tragen beide Methoden; für das Lowering sind sie derselbe Fall
                // (Empfänger als Parameter 0), nur die Member-Liste steckt woanders im AST.
                var (typeName, members) = decl switch
                {
                    ClassDecl c when c.Generics.Length == 0 => (c.Name, c.Members),
                    StructDecl v when v.Generics.Length == 0 => (v.Name, v.Members),
                    EnumDecl e when e.Generics.Length == 0 => (e.Name, e.Methods.Cast<Decl>().ToArray()),
                    // Default-Methoden eines Interfaces sind gewoehnliche Funktionen mit dem
                    // Empfaenger als Parameter 0 — nur dass dessen statischer Typ das Interface
                    // selbst ist. Ein 'this.foo()' darin wird damit zu einem callvirt, und das ist
                    // richtig: welche Implementierung laeuft, steht erst zur Laufzeit fest.
                    // Abstrakte Methoden (ohne Rumpf) fallen unten durch die Body-Pruefung.
                    InterfaceDecl i when i.Generics.Length == 0 => (i.Name, i.Members.Cast<Decl>().ToArray()),
                    _ => (null, null),
                };
                if (typeName is null || members is null) continue;
                if (module.Members.LookupLocal(typeName) is not TypeSymbol type) continue;

                foreach (var member in members)
                {
                    if (member is not FunctionDecl method) continue;
                    if (method.Generics.Length > 0 || method.Body is null) continue;
                    if (type.Members.LookupLocal(method.Name) is not FunctionSymbol symbol) continue;

                    ids[symbol] = new FunctionId(pending.Count);
                    pending.Add((method, NameMangling.ForMethod(module, typeName, method.Name),
                        method.IsStatic ? null : type));
                }
            }
        }

        // Globals werden VOR den Rumpfen gesammelt: eine Funktion darf eine Konstante lesen, die
        // weiter unten im Quelltext steht. Dieselbe Zwei-Phasen-Form wie bei den FunctionIds.
        try
        {
            globals.Collect(compilation, types, typeTable);
        }
        catch (UnsupportedConstructException ex)
        {
            de.Report(LoweringDiagnostics.NotSupported, Severity.Error, ex.Span, ex.Message);
            return null;
        }

        // Angehobene Lambdas kommen ganz ans Ende der Funktionsliste — hinter die geschriebenen
        // Funktionen UND hinter den Global-Initialisierer. Die Reihenfolge ist kein Geschmack:
        // die Position IST die FunctionId (ADR-013), und ein Lambda im Initialisierer
        // (`let f = () => 1;`) wuerde sonst seine eigene Id verschieben.
        var lambdas = new LambdaTable(pending.Count + (globals.IsEmpty ? 0 : 1));

        // Pass 2 — Bodies. Scope-Grenzen werden gemeldet, nicht geworfen: der Nutzer soll alle
        // fehlenden Konstrukte seines Programms in einem Durchlauf sehen, nicht eines pro Aufruf.
        var functions = new List<IrFunction>(pending.Count);
        var reported = new HashSet<(Span Span, string Message)>();
        foreach (var (decl, name, receiver) in pending)
        {
            try
            {
                functions.Add(new FunctionLowerer(decl, name, types, ids, imports, typeTable,
                    NoSubstitution, globals, lambdas, receiver).Run());
            }
            catch (UnsupportedConstructException ex)
            {
                // Eine Scope-Grenze im Layout eines Typs trifft jede Funktion, die ihn benutzt —
                // gemeldet werden soll sie einmal. Der Nutzer soll alle FEHLENDEN KONSTRUKTE seines
                // Programms sehen, nicht jede Stelle, an der dasselbe fehlt.
                if (reported.Add((ex.Span, ex.Message)))
                    de.Report(LoweringDiagnostics.NotSupported, Severity.Error, ex.Span, ex.Message);
                failed = true;
            }
        }

        // Eine übersprungene Funktion würde die FunctionIds der folgenden verschieben — der
        // Modulaufbau ist damit nicht mehr rettbar. Kein Teilergebnis zurückgeben.
        if (failed) return null;

        FunctionId? globalInit = null;
        if (!globals.IsEmpty)
        {
            try
            {
                globalInit = new FunctionId(functions.Count);
                functions.Add(GlobalInitializer.Build(globals, types, ids, imports, typeTable, lambdas));
            }
            catch (UnsupportedConstructException ex)
            {
                de.Report(LoweringDiagnostics.NotSupported, Severity.Error, ex.Span, ex.Message);
                return null;
            }
        }

        // Zuletzt die angehobenen Lambdas — und das ist eine Worklist, keine Schleife: ein Lambda
        // in einem Lambda meldet waehrend seines eigenen Lowerings ein weiteres an.
        if (!lambdas.IsEmpty)
        {
            try
            {
                functions.AddRange(lambdas.LowerAll(types, ids, imports, typeTable, globals));
            }
            catch (UnsupportedConstructException ex)
            {
                de.Report(LoweringDiagnostics.NotSupported, Severity.Error, ex.Span, ex.Message);
                return null;
            }
        }

        // Types nach dem Lowering eingesammelt, nicht davor: die Tabelle enthält nur, was
        // tatsächlich benutzt wurde — eine deklarierte, nie instanziierte Klasse gehört nicht in
        // den Bytecode. Gleiche Regel wie bei den Imports.
        var result = new IrModule(functions)
        {
            EntryFunction = entry, Imports = imports.Used, Types = typeTable.Defs,
            Globals = globals.Defs, GlobalInit = globalInit,
            Impls = BuildImpls(typeTable, binding, ids, de, ref failed),
        };
        if (failed) return null;
        if (verify ?? VerifyByDefault) IrVerifier.VerifyOrThrow(result);
        return result;
    }

    /// <summary>
    /// Die vtable-Zeilen: fuer jede internierte Klasse und jedes internierte Interface, das sie
    /// implementiert, Slot fuer Slot die Zielfunktion.
    ///
    /// <para><b>Nach</b> dem Lowering, weil erst dann feststeht, welche Typen ueberhaupt im
    /// Bytecode landen — dieselbe Regel wie bei Types und Imports: eine deklarierte, nie benutzte
    /// Klasse gehoert nicht hinein. Interfaces sind zu diesem Zeitpunkt bereits interniert, weil
    /// jedes <c>mkiface</c> und <c>callvirt</c> ihre Id schon beim Lowern gebraucht hat.</para>
    ///
    /// <para><b>Die Aufloesungsreihenfolge faellt hier, nicht zur Laufzeit</b> (Sprache.md §3.5:
    /// eigenes Member vor Interface-Default). Der Dispatch findet damit einen fertigen
    /// Funktionsindex vor und muss nichts suchen.</para>
    ///
    /// <para>Deterministisch sortiert: die Zeilen landen als Sektion im Bytecode, und ADR-013
    /// verlangt byte-identischen Output bei gleichem Input. Die Aufzaehlungsreihenfolge eines
    /// Dictionary erfuellt das nicht.</para>
    /// </summary>
    private static List<IrImpl> BuildImpls(TypeTable typeTable, BindingResult binding,
        Dictionary<FunctionSymbol, FunctionId> ids, DiagnosticEngine de, ref bool failed)
    {
        var impls = new List<IrImpl>();
        var interned = typeTable.Interned.ToList();
        var interfaces = interned.Where(t => t.Symbol.Kind == TypeSymbolKind.Interface).ToList();
        if (interfaces.Count == 0) return impls;

        foreach (var (type, typeId) in interned
                     .Where(t => t.Symbol.Kind is TypeSymbolKind.Class or TypeSymbolKind.Struct
                                 or TypeSymbolKind.Enum)
                     .OrderBy(t => t.Id.Value))
        {
            foreach (var (iface, ifaceId) in interfaces.OrderBy(t => t.Id.Value))
            {
                if (!Conformance.Implements(type, iface, binding)) continue;

                var slots = typeTable.MethodSlotsOf(ifaceId);
                var methods = new FunctionId[slots.Length];
                var complete = true;

                for (var i = 0; i < slots.Length; i++)
                {
                    // Eigenes Member schlaegt Default — Sprache.md §3.5.
                    var target = Resolve(type, slots[i], ids) ?? Resolve(iface, slots[i], ids);
                    if (target is { } id) { methods[i] = id; continue; }

                    // Die Sema hat Konformanz bereits geprueft (LYR-SEM). Fehlt hier trotzdem
                    // etwas, ist es eine Lowering-Luecke — etwa eine generische oder rumpflose
                    // Implementierung, die Pass 1 uebersprungen hat.
                    de.Report(LoweringDiagnostics.NotSupported, Severity.Error,
                        type.Declaration?.Span ?? default,
                        $"'{type.Name}' implements '{iface.Name}', but its '{slots[i]}' is not "
                        + "lowerable by this compiler version yet (generic or bodiless)");
                    complete = false;
                    break;
                }

                if (complete) impls.Add(new IrImpl(typeId, ifaceId, methods));
                else failed = true;
            }
        }

        return impls;
    }

    private static FunctionId? Resolve(TypeSymbol owner, string method,
        Dictionary<FunctionSymbol, FunctionId> ids) =>
        owner.Members.LookupLocal(method) is FunctionSymbol symbol
        && ids.TryGetValue(symbol, out var id)
            ? id
            : null;
}
