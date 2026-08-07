using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Ein Feld-Default ist ein Ausdruck und wird geprüft wie jeder andere.
///
/// <para><b>Er wurde von der Sema nie besucht.</b> Ein falsch typisierter Default war deshalb kein
/// Fehler — und sobald ein Initialisierer das Feld wegließ, wurde daraus ein Compiler-<b>Absturz</b>
/// im Lowering: dort wird der Default an der Konstruktionsstelle ausgewertet, die Seitentabelle
/// kannte seinen Typ nicht, also <c>ErrorType</c>, also „ir: type not lowerable: &lt;error&gt;".
/// Und weil keine Diagnose gemeldet war, sagte <c>lyric check</c> vorher „ok".</para>
///
/// <para>Aufgefallen ist es beim Bau von <c>console.lines()</c>, dessen <c>LineIterator { }</c>
/// genau das tut. Die Laufzeit-Seite steht in <c>Lyric.Tests.Vm.StructTests</c>.</para>
/// </summary>
public class DefaultFieldTests
{
    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    private static void Reports(string code, string source) =>
        Assert.Contains(Check(source).Diagnostics, d => d.Code == code);

    [Fact]
    public void A_default_of_the_wrong_type_is_a_diagnostic_not_a_crash() =>
        Reports("LYR-SEM0001", """
            class K { a: int = "nope", }
            fn main(): int { return 0; }
            """);

    [Fact]
    public void A_struct_default_is_checked_as_well() =>
        // Beide Deklarationsarten laufen durch CheckMethods — ohne diesen Test bliebe ungeprüft,
        // ob der Zweig für 'struct' überhaupt erreicht wird.
        Reports("LYR-SEM0001", """
            struct V { n: bool = 1, }
            fn main(): int { return 0; }
            """);

    [Fact]
    public void An_unknown_name_in_a_default_is_reported() =>
        Reports("LYR-SEM0002", """
            class K { a: int = gibtsNicht, }
            fn main(): int { return 0; }
            """);

    [Fact]
    public void A_correct_default_passes()
    {
        var de = Check("""
            class K { a: int = 5, b: string = "x", }
            fn main(): int { return 0; }
            """);

        Assert.False(de.HasErrors,
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }
}
