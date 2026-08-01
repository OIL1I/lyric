using Lyric.AST;
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
/// Id und meldet das als „nicht unterstützt" statt still falschen Code zu erzeugen.</para>
/// </summary>
public static class ModuleLowerer
{
    private static readonly Dictionary<GenericParamSymbol, LyrType> NoSubstitution =
        new(ReferenceEqualityComparer.Instance);

    public static IrModule Lower(Compilation compilation, TypeResult types, bool verify = true)
    {
        var pending = new List<(FunctionDecl Decl, string Name)>();
        var ids = new Dictionary<FunctionSymbol, FunctionId>(ReferenceEqualityComparer.Instance);

        // Pass 1 — Funktionstabelle. Die Reihenfolge ist Modul- dann Deklarations-Reihenfolge und
        // damit deterministisch: FunctionIds landen als Indizes im Bytecode (ADR-013).
        foreach (var module in compilation.Modules)
        {
            foreach (var decl in compilation.AstOf(module).Declarations)
            {
                if (decl is not FunctionDecl function) continue;
                if (function.Body is null) continue;
                if (function.Generics.Length > 0) continue;
                if (module.Members.LookupLocal(function.Name) is not FunctionSymbol symbol) continue;

                ids[symbol] = new FunctionId(pending.Count);
                pending.Add((function, NameMangling.ForFunction(module, function.Name)));
            }
        }

        // Pass 2 — Bodies.
        var functions = new List<IrFunction>(pending.Count);
        foreach (var (decl, name) in pending)
            functions.Add(new FunctionLowerer(decl, name, types, ids, NoSubstitution).Run());

        var result = new IrModule(functions);
        if (verify) IrVerifier.VerifyOrThrow(result);
        return result;
    }
}
