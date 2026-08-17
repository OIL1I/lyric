using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;

namespace Lyric.Tests.Parsing;

/// <summary>
/// When a member of a struct or class needs the <c>,</c> that separates it from the next.
///
/// <para>Only a FIELD does. Without the rule <c>a: int b: int</c> would be a valid line, and that is
/// the whole reason it exists; everything else has already closed itself, with <c>}</c> or with
/// <c>;</c>.</para>
///
/// <para>THE COUNTER-CHECKS ARE THE MORE IMPORTANT HALF. A rule that is too permissive costs no
/// diagnostic — it accepts two fields written as one and reads them wrongly, which shows up much
/// later or not at all.</para>
/// </summary>
public class MemberSeparatorTests
{
    private static IReadOnlyList<Diagnostic> Parse(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        new Parser(sm, id, de).ParseModule();
        return de.Diagnostics;
    }

    [Fact]
    public void Two_bodiless_methods_need_no_comma()
    {
        // The case that made the rule visible: a class of host objects declares its surface and
        // nothing else. Before this, each line needed ';' AND ',' in a row.
        Assert.Empty(Parse("""
            class Builder {
                fn addExecutable(entry: string, output: string): int;
                fn addTest(entry: string): int;
            }
            """));
    }

    [Fact]
    public void A_bodiless_method_before_a_field_needs_no_comma()
    {
        // The method closed itself. What follows is a field, and the field's own separator is a
        // different question.
        Assert.Empty(Parse("""
            class Mixed {
                fn probe(): int;
                value: int,
            }
            """));
    }

    [Fact]
    public void The_comma_after_a_bodiless_method_is_still_accepted()
    {
        // The rule was loosened, not replaced. Every file that was valid before still is.
        Assert.Empty(Parse("""
            class Builder {
                fn addExecutable(entry: string, output: string): int;,
                fn addTest(entry: string): int;
            }
            """));
    }

    [Fact]
    public void Two_fields_still_need_one()
    {
        // The counter-check that gives the rule its purpose. Without it these two lines would be
        // read as one field of a type nobody wrote.
        var diagnostics = Parse("""
            class Point {
                x: int
                y: int
            }
            """);

        Assert.Contains(diagnostics, d => d.Code == "LYR-PAR0029");
    }

    [Fact]
    public void A_field_before_a_method_still_needs_one()
    {
        var diagnostics = Parse("""
            class Counter {
                count: int
                fn value(): int { return this.count; }
            }
            """);

        Assert.Contains(diagnostics, d => d.Code == "LYR-PAR0029");
    }

    [Fact]
    public void A_block_body_and_a_static_let_keep_their_freedom()
    {
        // Unchanged, and checked so a change to the condition cannot silently take it away.
        Assert.Empty(Parse("""
            class Both {
                static let VERSION = 1;
                fn value(): int { return 2; }
                fn other(): int { return 3; }
            }
            """));
    }
}
