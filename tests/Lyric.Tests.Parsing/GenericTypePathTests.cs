using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;

namespace Lyric.Tests.Parsing;

/// <summary>
/// When a <c>&lt;</c> is a type argument list and when it is a comparison.
///
/// <para>The rule is the same as for <c>f&lt;int&gt;()</c>: type arguments when only type-like tokens
/// stand between the <c>&lt;</c> and its match and a <c>.</c> follows directly. In doubt a
/// comparison.</para>
///
/// <para>The counter-checks are the more important half. A detection that is too greedy costs no
/// diagnostic but a wrong reading — <c>a &lt; b</c> would no longer be a comparison, and that would show
/// only in the sema, if at all.</para>
/// </summary>
public class GenericTypePathTests
{
    private static (Module Ast, IReadOnlyList<Diagnostic> Diagnostics) Parse(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        return (new Parser(sm, id, de).ParseModule(), de.Diagnostics);
    }

    private static Expr FirstInitializer(Module ast) =>
        ast.Declarations.OfType<FunctionDecl>().Single(f => f.Name == "main")
            .Body!.Statements.OfType<BindingStmt>().First().Initializer!;

    // ------------------------------------------------------------------ Typpfad

    [Fact]
    public void A_generic_type_path_before_a_dot_is_a_type_path()
    {
        var (ast, diagnostics) = Parse("fn main(): int { let p = Pair<int>.of(3); return 0; }");

        Assert.Empty(diagnostics);
        var call = Assert.IsType<CallExpr>(FirstInitializer(ast));
        var member = Assert.IsType<MemberExpr>(call.Callee);
        var path = Assert.IsType<TypePathExpr>(member.Target);

        Assert.Equal(["Pair"], path.Path);
        Assert.Single(path.TypeArguments);
        Assert.Equal("of", member.Member);
    }

    /// <summary>A dotted path: the type may come from a module.</summary>
    [Fact]
    public void A_module_qualified_type_path_keeps_its_segments()
    {
        var (ast, _) = Parse("fn main(): int { let p = game.Pair<int>.of(3); return 0; }");

        var call = Assert.IsType<CallExpr>(FirstInitializer(ast));
        var path = Assert.IsType<TypePathExpr>(((MemberExpr)call.Callee).Target);
        Assert.Equal(["game", "Pair"], path.Path);
    }

    /// <summary>Nested: a <c>&gt;&gt;</c> closes two levels at once.</summary>
    [Fact]
    public void A_nested_type_argument_parses()
    {
        var (ast, diagnostics) = Parse(
            "fn main(): int { let p = Pair<Pair<int>>.of(3); return 0; }");

        Assert.Empty(diagnostics);
        Assert.IsType<TypePathExpr>(((MemberExpr)((CallExpr)FirstInitializer(ast)).Callee).Target);
    }

    /// <summary>Two arguments.</summary>
    [Fact]
    public void Two_type_arguments_parse()
    {
        var (ast, _) = Parse("fn main(): int { let p = Two<int, bool>.of(1, true); return 0; }");

        var path = Assert.IsType<TypePathExpr>(
            ((MemberExpr)((CallExpr)FirstInitializer(ast)).Callee).Target);
        Assert.Equal(2, path.TypeArguments.Length);
    }

    // ------------------------------------------------------------------ counter-checks

    /// <summary>Without a following dot it is a comparison, which is the normal case in every
    /// Programm.</summary>
    [Fact]
    public void A_bare_comparison_is_not_a_type_path()
    {
        var (ast, diagnostics) = Parse("fn main(): int { let c = a < b; return 0; }");

        Assert.Empty(diagnostics);
        Assert.IsType<BinaryExpr>(FirstInitializer(ast));
    }

    /// <summary>
    /// The nasty form: a comparison chain really followed by a dot. <c>a &lt; b &gt; c.d</c> has only
    /// type-like tokens between the <c>&lt;</c> and the <c>&gt;</c>, but what follows is <c>c</c> rather
    /// than <c>.</c>, so it stays a comparison.
    /// </summary>
    [Fact]
    public void A_comparison_chain_followed_by_a_member_access_stays_a_comparison()
    {
        var (ast, diagnostics) = Parse("fn main(): int { let c = a < b > c.d; return 0; }");

        Assert.Empty(diagnostics);
        Assert.IsType<BinaryExpr>(FirstInitializer(ast));
    }

    /// <summary>A struct initializer stays a struct initializer: there a <c>{</c> follows rather than a
    /// <c>.</c>, and the older branch applies first.</summary>
    [Fact]
    public void A_generic_struct_init_is_still_a_struct_init()
    {
        var (ast, _) = Parse("fn main(): int { let p = Pair<int> { a = 1 }; return 0; }");
        Assert.IsType<StructInitExpr>(FirstInitializer(ast));
    }

    /// <summary>And the non-generic type path stays an identifier: it does not need the new
    /// node, and producing it anyway would mean answering one question at two places.</summary>
    [Fact]
    public void A_plain_static_call_stays_an_identifier()
    {
        var (ast, _) = Parse("fn main(): int { let p = P.neu(); return 0; }");

        var member = Assert.IsType<MemberExpr>(((CallExpr)FirstInitializer(ast)).Callee);
        Assert.IsType<IdentifierExpr>(member.Target);
    }

    // ------------------------------------------------------------------ Dumper

    /// <summary>
    /// The <c>AstDumper</c> throws on every node it does not know (<c>lyrc ast</c>). Without this test
    /// the new node would be a crash there that no other test touches.
    /// </summary>
    [Fact]
    public void The_dumper_knows_the_new_node()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "fn main(): int { let p = Pair<int>.of(3); return 0; }");
        var de = new DiagnosticEngine(sm);
        var ast = new Parser(sm, id, de).ParseModule();

        Assert.Contains("TypePath Pair", AstDumper.Dump(ast, sm), StringComparison.Ordinal);
    }
}
