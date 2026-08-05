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
    private static readonly Dictionary<GenericParamSymbol, LyrType> NoSubstitution =
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
        FunctionId? entry = null;

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
                if (function.Name == "main" && function.Parameters.Length == 0) entry = id;
            }

            // Methoden sind gewöhnliche Funktionen mit dem Empfänger als Parameter 0 — dieselbe
            // Konvention wie CIL. Der Unterschied zwischen Instanz- und static-Methode ist damit
            // allein die Parameterliste, und P3 muss für die vtable nur noch entscheiden, WELCHE
            // Funktion gerufen wird, nicht wie sie aussieht.
            foreach (var decl in compilation.AstOf(module).Declarations)
            {
                if (decl is not ClassDecl cls) continue;
                if (cls.Generics.Length > 0) continue;
                if (module.Members.LookupLocal(cls.Name) is not TypeSymbol type) continue;

                foreach (var member in cls.Members)
                {
                    if (member is not FunctionDecl method) continue;
                    if (method.Generics.Length > 0 || method.Body is null) continue;
                    if (type.Members.LookupLocal(method.Name) is not FunctionSymbol symbol) continue;

                    ids[symbol] = new FunctionId(pending.Count);
                    pending.Add((method, NameMangling.ForMethod(module, cls.Name, method.Name),
                        method.IsStatic ? null : type));
                }
            }
        }

        // Pass 2 — Bodies. Scope-Grenzen werden gemeldet, nicht geworfen: der Nutzer soll alle
        // fehlenden Konstrukte seines Programms in einem Durchlauf sehen, nicht eines pro Aufruf.
        var functions = new List<IrFunction>(pending.Count);
        var reported = new HashSet<(Span Span, string Message)>();
        var failed = false;
        foreach (var (decl, name, receiver) in pending)
        {
            try
            {
                functions.Add(new FunctionLowerer(decl, name, types, ids, imports, typeTable,
                    NoSubstitution, receiver).Run());
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

        // Types nach dem Lowering eingesammelt, nicht davor: die Tabelle enthält nur, was
        // tatsächlich benutzt wurde — eine deklarierte, nie instanziierte Klasse gehört nicht in
        // den Bytecode. Gleiche Regel wie bei den Imports.
        var result = new IrModule(functions)
        {
            EntryFunction = entry, Imports = imports.Used, Types = typeTable.Defs,
        };
        if (verify ?? VerifyByDefault) IrVerifier.VerifyOrThrow(result);
        return result;
    }
}
