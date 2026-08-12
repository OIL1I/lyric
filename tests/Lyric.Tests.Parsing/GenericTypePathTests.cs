using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;

namespace Lyric.Tests.Parsing;

/// <summary>
/// Wann <c>&lt;</c> eine Typargument-Liste ist und wann ein Vergleich (Sprache.md §6.1/§6.2).
///
/// <para><b>Die Regel ist dieselbe wie für <c>f&lt;int&gt;()</c>:</b> Typargumente, wenn zwischen
/// <c>&lt;</c> und dem passenden <c>&gt;</c> ausschließlich typartige Tokens stehen und direkt
/// danach ein <c>.</c> folgt. Im Zweifel ein Vergleich.</para>
///
/// <para><b>Die Gegenproben sind die wichtigere Hälfte.</b> Eine zu gierige Erkennung kostet keine
/// Diagnose, sondern eine falsche Deutung — <c>a &lt; b</c> wäre dann kein Vergleich mehr, und das
/// fiele erst in der Sema auf, wenn überhaupt.</para>
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

    /// <summary>Ein dotted Pfad — der Typ kann aus einem Modul kommen.</summary>
    [Fact]
    public void A_module_qualified_type_path_keeps_its_segments()
    {
        var (ast, _) = Parse("fn main(): int { let p = game.Pair<int>.of(3); return 0; }");

        var call = Assert.IsType<CallExpr>(FirstInitializer(ast));
        var path = Assert.IsType<TypePathExpr>(((MemberExpr)call.Callee).Target);
        Assert.Equal(["game", "Pair"], path.Path);
    }

    /// <summary>Verschachtelt: <c>&gt;&gt;</c> schließt zwei Ebenen auf einmal.</summary>
    [Fact]
    public void A_nested_type_argument_parses()
    {
        var (ast, diagnostics) = Parse(
            "fn main(): int { let p = Pair<Pair<int>>.of(3); return 0; }");

        Assert.Empty(diagnostics);
        Assert.IsType<TypePathExpr>(((MemberExpr)((CallExpr)FirstInitializer(ast)).Callee).Target);
    }

    /// <summary>Zwei Argumente.</summary>
    [Fact]
    public void Two_type_arguments_parse()
    {
        var (ast, _) = Parse("fn main(): int { let p = Two<int, bool>.of(1, true); return 0; }");

        var path = Assert.IsType<TypePathExpr>(
            ((MemberExpr)((CallExpr)FirstInitializer(ast)).Callee).Target);
        Assert.Equal(2, path.TypeArguments.Length);
    }

    // ------------------------------------------------------------------ Gegenproben

    /// <summary>Ohne folgenden Punkt ist es ein Vergleich — das ist der Normalfall in jedem
    /// Programm.</summary>
    [Fact]
    public void A_bare_comparison_is_not_a_type_path()
    {
        var (ast, diagnostics) = Parse("fn main(): int { let c = a < b; return 0; }");

        Assert.Empty(diagnostics);
        Assert.IsType<BinaryExpr>(FirstInitializer(ast));
    }

    /// <summary>
    /// Die böse Form: eine Vergleichskette, hinter der wirklich ein Punkt steht.
    /// <c>a &lt; b &gt; c.d</c> hat zwischen <c>&lt;</c> und <c>&gt;</c> nur typartige Tokens —
    /// aber danach steht <c>c</c> und nicht <c>.</c>, also bleibt es ein Vergleich.
    /// </summary>
    [Fact]
    public void A_comparison_chain_followed_by_a_member_access_stays_a_comparison()
    {
        var (ast, diagnostics) = Parse("fn main(): int { let c = a < b > c.d; return 0; }");

        Assert.Empty(diagnostics);
        Assert.IsType<BinaryExpr>(FirstInitializer(ast));
    }

    /// <summary>Ein Struct-Init bleibt ein Struct-Init — dort folgt ein <c>{</c> statt eines
    /// <c>.</c>, und der ältere Zweig greift zuerst.</summary>
    [Fact]
    public void A_generic_struct_init_is_still_a_struct_init()
    {
        var (ast, _) = Parse("fn main(): int { let p = Pair<int> { a = 1 }; return 0; }");
        Assert.IsType<StructInitExpr>(FirstInitializer(ast));
    }

    /// <summary>Und der nicht-generische Typpfad bleibt ein Bezeichner — er braucht den neuen
    /// Knoten nicht, und ihn trotzdem zu erzeugen hieße, eine Frage an zwei Stellen zu
    /// beantworten.</summary>
    [Fact]
    public void A_plain_static_call_stays_an_identifier()
    {
        var (ast, _) = Parse("fn main(): int { let p = P.neu(); return 0; }");

        var member = Assert.IsType<MemberExpr>(((CallExpr)FirstInitializer(ast)).Callee);
        Assert.IsType<IdentifierExpr>(member.Target);
    }

    // ------------------------------------------------------------------ Dumper

    /// <summary>
    /// Der <c>AstDumper</c> wirft bei jedem Knoten, den er nicht kennt (<c>lyrc ast</c>). Ohne
    /// diesen Test wäre der neue Knoten dort ein Absturz, den kein anderer Test berührt.
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
