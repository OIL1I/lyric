using Lyric.Core;
using Lyric.Parsing;

namespace Lyric.Tests.Parsing;

/// <summary>
/// Two forms Lyric does not have, which therefore have to SAY that they do not exist.
///
/// <para>Both used to report something else. An attribute on a parameter was read as a parameter name;
/// the body was then missing and the compiler spoke of native declarations — to someone who wanted to
/// write an attribute. <c>interface B :: [A]</c> ran into a message about parameter parentheses.</para>
///
/// <para>That is no blemish: a diagnostic pointing at the wrong cause costs more time than none at all.
/// Whoever reads it searches at the place it names.</para>
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

    /// <summary>On a declaration it always said so; here it stands as the reference point.
    /// </summary>
    [Fact]
    public void An_attribute_on_a_declaration_says_attributes_are_post_v1() =>
        Assert.Contains(Parse("@test\nfn f(): int { return 1; }"),
            d => d.Code == "LYR-PAR0038");

    /// <summary>
    /// On a parameter it used to say <c>LYR-SEM0051</c> — "only standard-library modules may declare
    /// native functions". The parser had lost the body.
    /// </summary>
    [Fact]
    public void An_attribute_on_a_parameter_says_the_same_thing()
    {
        var diagnostics = Parse("fn nimm(@noCapture f: fn() -> int): int { return f(); }");

        Assert.Contains(diagnostics, d => d.Code == "LYR-PAR0038");

        // And the body is NOT lost in the process: no follow-up message about a missing declaration.
        // Without this promise the new message would only be an additional one.
        Assert.Single(diagnostics);
    }

    /// <summary>Several attributes on one parameter give several messages and no crash: the loop has to
    /// terminate.</summary>
    [Fact]
    public void Several_attributes_on_one_parameter_each_get_a_diagnostic() =>
        Assert.Equal(2, Parse("fn f(@a @b x: int): int { return x; }")
            .Count(d => d.Code == "LYR-PAR0038"));

    // ------------------------------------------------------------------ interface inheritance

    /// <summary>
    /// The message names the way out, because there is one: <c>std.core</c> solves the same with two
    /// constraints side by side (<c>K :: [Hashable&lt;K&gt;, Equatable&lt;K&gt;]</c>).
    /// </summary>
    [Fact]
    public void An_interface_conformance_list_says_there_is_no_interface_inheritance()
    {
        var diagnostics = Parse("interface A { fn a(): int; }\ninterface B :: [A] { fn b(): int; }");

        var reported = Assert.Single(diagnostics, d => d.Code == "LYR-PAR0039");
        Assert.Contains("no interface inheritance", reported.Message, StringComparison.Ordinal);
        Assert.Contains("[A, B]", reported.Message, StringComparison.Ordinal);

        // ONE message per cause: the conformance list is read and discarded, or the parser would stumble
        // over '[A]' a second time.
        Assert.Single(diagnostics);
    }

    /// <summary>
    /// The counter-check. A <c>::</c> on a CLASS is valid and must not be hit by this message, or half
    /// the stdlib would be a syntax error.
    /// </summary>
    [Fact]
    public void A_conformance_list_on_a_class_is_still_fine() =>
        Assert.Empty(Parse("""
            interface A { fn a(): int; }
            class K :: [A] { fn a(): int { return 1; } }
            """));

    /// <summary>And an ordinary interface stays ordinary.</summary>
    [Fact]
    public void A_plain_interface_parses() =>
        Assert.Empty(Parse("interface A<T> { fn a(): T; }"));
}
