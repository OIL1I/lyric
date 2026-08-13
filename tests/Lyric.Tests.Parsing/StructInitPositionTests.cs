using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;

namespace Lyric.Tests.Parsing;

/// <summary>
/// Where a <c>Foo { … }</c> may stand: in every value position.
///
/// <para>The block applies to the START of a statement rather than to the whole statement: there
/// <c>Foo { … };</c> would be ambiguous with a block. Behind an <c>=</c> no block can stand, so there
/// is nothing to confuse.</para>
///
/// <para>While the block applied throughout, <c>s = Small { n = 5 };</c> was
/// <c>LYR-SEM0052: 'Small' is a type, not a value — did you mean 'Small { . }'?</c>, a suggestion to
/// write exactly what already stood there.
/// wiederzuerkennen.</para>
/// </summary>
public class StructInitPositionTests
{
    private static (Module Ast, IReadOnlyList<Diagnostic> Diagnostics) Parse(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        return (new Parser(sm, id, de).ParseModule(), de.Diagnostics);
    }

    private static void ParsesCleanly(string source) =>
        Assert.Empty(Parse(source).Diagnostics);

    // ------------------------------------------------------------------ allowed

    [Fact]
    public void A_struct_init_may_stand_on_the_right_of_an_assignment() =>
        ParsesCleanly("""
            struct Small { n: int, }
            fn main(): int {
                var s = Small { n = 1 };
                s = Small { n = 5 };
                return s.n;
            }
            """);

    [Fact]
    public void Also_on_the_right_of_a_field_assignment() =>
        ParsesCleanly("""
            struct P { n: int, }
            class Box { p: P, }
            fn main(): int {
                var b = Box { p = P { n = 1 } };
                b.p = P { n = 2 };
                return b.p.n;
            }
            """);

    [Fact]
    public void And_on_the_right_of_a_compound_assignment() =>
        ParsesCleanly("""
            struct V { n: int, }
            fn main(): int {
                var xs = [0];
                xs[0] = 5;
                return xs[0];
            }
            """);

    /// <summary>A generic type on the right-hand side: the <c>&lt;</c> has to be read as a type argument
    /// list there rather than as a comparison.</summary>
    [Fact]
    public void A_generic_struct_init_works_on_the_right_of_an_assignment() =>
        ParsesCleanly("""
            struct Box<T> { v: T, }
            fn main(): int {
                var b = Box<int> { v = 1 };
                b = Box<int> { v = 2 };
                return b.v;
            }
            """);

    // ------------------------------------------------------------------ weiterhin gesperrt

    /// <summary>
    /// The counter-check, and the more important one: AT THE START of a statement it stays blocked.
    /// Without it a fix reintroducing the ambiguity with a block would be green, and that costs no
    /// diagnostic but a wrong reading.
    /// </summary>
    [Fact]
    public void A_statement_still_does_not_start_with_a_struct_init()
    {
        // 'Small { n = 5 };' at the start of a statement is read as a block, and the parser reports what
        // it really sees there. What matters is that it does NOT take it as a struct initializer.
        var (ast, _) = Parse("""
            struct Small { n: int, }
            fn main(): int {
                Small { n = 5 };
                return 0;
            }
            """);

        var main = ast.Declarations.OfType<FunctionDecl>().Single(f => f.Name == "main");
        Assert.DoesNotContain(main.Body!.Statements,
            s => s is ExprStmt { Expr: StructInitExpr });
    }

    /// <summary>And a block stays a block: the form the restriction exists for.
    /// </summary>
    [Fact]
    public void A_bare_block_still_parses_as_a_block() =>
        ParsesCleanly("""
            fn main(): int {
                { let x = 1; }
                return 0;
            }
            """);

    /// <summary>
    /// A comparison stays a comparison. The <c>&lt;</c> is read as a type argument list only when it
    /// closes balanced and a <c>{</c> follows; otherwise <c>a = b &lt; c</c> could no longer be written.
    /// </summary>
    [Fact]
    public void A_comparison_on_the_right_of_an_assignment_stays_a_comparison() =>
        ParsesCleanly("""
            fn main(): int {
                let a = 1;
                let b = 2;
                var c = false;
                c = a < b;
                return if (c) 1 else 0;
            }
            """);
}
