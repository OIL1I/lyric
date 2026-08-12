using Lyric.Core;
using Lyric.Parsing;

namespace Lyric.Tests.Parsing;

/// <summary>
/// Zwei Formen, die Lyric nicht hat — und die deshalb <b>sagen müssen, dass es sie nicht gibt</b>.
///
/// <para><b>Beide meldeten bis 2026-08-11 etwas anderes.</b> Ein Attribut an einem Parameter wurde
/// als Parametername gelesen; danach fehlte der Rumpf, und der Compiler sprach von nativen
/// Deklarationen — zu jemandem, der ein Attribut schreiben wollte.
/// <c>interface B :: [A]</c> lief in eine Meldung über Parameter-Klammern.</para>
///
/// <para>Das ist kein Schönheitsfehler: eine Diagnose, die auf die falsche Ursache zeigt, kostet
/// mehr Zeit als gar keine. Wer sie liest, sucht an der genannten Stelle.</para>
/// </summary>
public class RejectedSyntaxTests
{
    private static IReadOnlyList<Diagnostic> Parse(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        new Parser(sm, id, de).ParseModule();
        return de.Diagnostics;
    }

    // ------------------------------------------------------------------ Attribute (§10)

    /// <summary>An einer Deklaration sagte es das schon immer — hier steht es als Bezugspunkt.
    /// </summary>
    [Fact]
    public void An_attribute_on_a_declaration_says_attributes_are_post_v1() =>
        Assert.Contains(Parse("@test\nfn f(): int { return 1; }"),
            d => d.Code == "LYR-PAR0038");

    /// <summary>
    /// An einem Parameter sagte es bis heute <c>LYR-SEM0051</c> — „only standard-library modules
    /// may declare native functions". Der Parser hatte den Rumpf verloren.
    /// </summary>
    [Fact]
    public void An_attribute_on_a_parameter_says_the_same_thing()
    {
        var diagnostics = Parse("fn nimm(@noCapture f: fn() -> int): int { return f(); }");

        Assert.Contains(diagnostics, d => d.Code == "LYR-PAR0038");

        // Und der Rumpf geht dabei NICHT verloren: keine Folgemeldung über eine fehlende
        // Deklaration. Ohne diese Zusicherung wäre die neue Meldung nur eine zusätzliche.
        Assert.Single(diagnostics);
    }

    /// <summary>Mehrere Attribute an einem Parameter ergeben mehrere Meldungen und keinen
    /// Absturz — die Schleife muss terminieren.</summary>
    [Fact]
    public void Several_attributes_on_one_parameter_each_get_a_diagnostic() =>
        Assert.Equal(2, Parse("fn f(@a @b x: int): int { return x; }")
            .Count(d => d.Code == "LYR-PAR0038"));

    // ------------------------------------------------------------------ Interface-Vererbung (§7)

    /// <summary>
    /// Die Meldung nennt den Ausweg, weil es einen gibt: <c>std.core</c> löst dasselbe mit zwei
    /// Constraints nebeneinander (<c>K :: [Hashable&lt;K&gt;, Equatable&lt;K&gt;]</c>, ADR-024).
    /// </summary>
    [Fact]
    public void An_interface_conformance_list_says_there_is_no_interface_inheritance()
    {
        var diagnostics = Parse("interface A { fn a(): int; }\ninterface B :: [A] { fn b(): int; }");

        var reported = Assert.Single(diagnostics, d => d.Code == "LYR-PAR0039");
        Assert.Contains("no interface inheritance", reported.Message, StringComparison.Ordinal);
        Assert.Contains("[A, B]", reported.Message, StringComparison.Ordinal);

        // EINE Meldung je Ursache: die Konformanzliste wird gelesen und verworfen, sonst
        // stolperte der Parser gleich noch einmal über '[A]'.
        Assert.Single(diagnostics);
    }

    /// <summary>
    /// Die Gegenprobe. Ein <c>::</c> an einer <b>Klasse</b> ist gültig und darf von dieser
    /// Meldung nicht getroffen werden — sonst wäre die halbe Stdlib ein Syntaxfehler.
    /// </summary>
    [Fact]
    public void A_conformance_list_on_a_class_is_still_fine() =>
        Assert.Empty(Parse("""
            interface A { fn a(): int; }
            class K :: [A] { fn a(): int { return 1; } }
            """));

    /// <summary>Und ein gewöhnliches Interface bleibt gewöhnlich.</summary>
    [Fact]
    public void A_plain_interface_parses() =>
        Assert.Empty(Parse("interface A<T> { fn a(): T; }"));
}
