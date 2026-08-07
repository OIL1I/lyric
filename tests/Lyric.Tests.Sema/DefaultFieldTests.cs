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

/// <summary>
/// Ein Typ-Parameter, den die Inferenz nicht binden kann, wird von der <b>Sema</b> gemeldet.
///
/// <para>Vorher wurde er still zu <c>ErrorType</c>, und erst das Lowering fiel darüber:
/// <c>LYR-IR0001: type argument 0 is not concrete ('&lt;error&gt;')</c>. <c>lyric check</c> sagte
/// dazu „ok", <c>lyric build</c> nicht — derselbe Riss zwischen Sema und Backend, gegen den
/// <c>AgreementTests</c> gebaut wurde, nur mit einer Diagnose am Ende statt einem Absturz.</para>
/// </summary>
public class InferenceDiagnosticTests
{
    private static string Diagnostics(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);

        var writer = new StringWriter();
        de.RenderText(writer);
        return writer.ToString();
    }

    [Fact]
    public void An_unbindable_type_parameter_is_reported_by_the_sema()
    {
        // T kommt in keinem Parameter vor — das kann auch die Konformanz-Unifikation nicht retten.
        var diagnostics = Diagnostics("""
            pub fn leer<T>(n: int): int { return n; }
            fn main(): int { return leer(3); }
            """);

        Assert.Contains("LYR-SEM0060", diagnostics);
        Assert.Contains("write it explicitly", diagnostics);
    }

    [Fact]
    public void An_explicit_type_argument_silences_it() =>
        Assert.Equal("", Diagnostics("""
            pub fn leer<T>(n: int): int { return n; }
            fn main(): int { return leer<int>(3); }
            """));

    [Fact]
    public void A_broken_argument_does_not_add_inference_noise()
    {
        // Wenn ein Argument selbst fehlerhaft ist, ist die Ursache gemeldet. Eine zweite Zeile
        // über ein Typargument wäre Folgerauschen — der häufigste Weg, wie Diagnosen unlesbar
        // werden.
        var diagnostics = Diagnostics("""
            pub fn id<T>(v: T): T { return v; }
            fn main(): int { return id(gibtsNicht); }
            """);

        Assert.Contains("LYR-SEM0002", diagnostics);
        Assert.DoesNotContain("LYR-SEM0060", diagnostics);
    }
}

/// <summary>
/// Ein Fehler <b>in</b> einem Typ zählt als gemeldet — die Poison-Regel, eine Ebene tiefer.
///
/// <para>Bisher galt sie nur, wenn der Typ selbst <c>ErrorType</c> war. Ein
/// <c>fn(int) -&gt; &lt;error&gt;</c> — außen intakt, innen kaputt — ging durch und erzeugte
/// Folgemeldungen, die die eigentliche Ursache zudeckten.</para>
///
/// <para>Aufgefallen an einem Block-Lambda ohne Rückgabetyp-Annotation: <b>drei</b> Diagnosen für
/// einen Fehler, und die einzige mit einem brauchbaren Hinweis (<c>LYR-SEM0046</c>, „add a return
/// type annotation") stand unten.</para>
/// </summary>
public class DiagnosticNoiseTests
{
    private static string[] Codes(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de.Diagnostics.Select(d => d.Code).ToArray();
    }

    private const string BlockLambda = """
        pub fn anwenden<T, U>(v: T, f: fn(T) -> U): U { let g = f; return g(v); }
        fn main(): int { let b = anwenden(3, (n: int) => { return n * 2; }); return 0; }
        """;

    [Fact]
    public void A_block_lambda_without_annotation_reports_the_helpful_code() =>
        Assert.Contains("LYR-SEM0046", Codes(BlockLambda));

    [Fact]
    public void It_does_not_also_complain_about_the_type_argument() =>
        // LYR-SEM0060 wäre hier Rauschen: die Ursache steht in SEM0046, samt Anleitung.
        Assert.DoesNotContain("LYR-SEM0060", Codes(BlockLambda));

    [Fact]
    public void It_does_not_also_complain_about_assignability() =>
        // "cannot assign 'fn(int) -> <error>' to 'fn(int) -> U'" sagt dem Leser nichts.
        Assert.DoesNotContain("LYR-SEM0001", Codes(BlockLambda));

    [Fact]
    public void The_annotated_form_compiles_cleanly() =>
        // Die Gegenprobe: was die Meldung vorschlägt, muss auch funktionieren — sonst wäre der
        // Hinweis falsch, und das ist schlimmer als kein Hinweis.
        Assert.Empty(Codes("""
            pub fn anwenden<T, U>(v: T, f: fn(T) -> U): U { let g = f; return g(v); }
            fn main(): int { return anwenden(3, (n: int): int => { return n * 2; }); }
            """));
}
