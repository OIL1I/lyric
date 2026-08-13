using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;

namespace Lyric.Tests.Parsing;

/// <summary>
/// Round brackets in type position.
///
/// <para>They are needed because <c>fn(A) -&gt; R</c> is the only type in the language open to the
/// right: <c>fn(int) -&gt; void[]</c> is a function returning <c>void[]</c>, and an array of function
/// values could not be written down at all — the type existed, an array literal of lambdas has it, it
/// was only not nameable.</para>
///
/// <para>No conflict with tuples arises: <c>TupleType</c> requires arity 2, so the place for <c>(T)</c>
/// was free. Rust needs <c>(T,)</c> for this.</para>
/// </summary>
public class GroupedTypeTests
{
    private static (TypeNode Type, DiagnosticEngine De) ParseType(string type)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("t.lyr", $"fn f(): {type} {{ }}");
        var de = new DiagnosticEngine(sm);
        var module = new Parser(sm, id, de).ParseModule();
        return (((FunctionDecl)module.Declarations[0]).ReturnType!, de);
    }

    [Fact]
    public void A_parenthesized_type_is_the_type_itself()
    {
        var (type, de) = ParseType("(int)");
        Assert.False(de.HasErrors);
        // No TupleType with one element: the inner node moves up unchanged.
        Assert.IsType<NamedType>(type);
    }

    [Fact]
    public void Parentheses_nest()
    {
        var (type, de) = ParseType("((int))");
        Assert.False(de.HasErrors);
        Assert.IsType<NamedType>(type);
    }

    [Fact]
    public void An_array_of_function_values_is_now_writable()
    {
        // The case the grouping exists for.
        var (type, de) = ParseType("(fn(int) -> void)[]");
        Assert.False(de.HasErrors);

        var array = Assert.IsType<ArrayType>(type);
        Assert.IsType<FunctionType>(array.Element);
    }

    [Fact]
    public void Without_parentheses_the_bracket_belongs_to_the_return_type()
    {
        // The precedence stays as it was: reversing it would silently turn 'fn(): int[]' into something
        // other than before.
        var (type, de) = ParseType("fn(int) -> void[]");
        Assert.False(de.HasErrors);

        var fn = Assert.IsType<FunctionType>(type);
        Assert.IsType<ArrayType>(fn.ReturnType);
    }

    [Fact]
    public void Two_elements_are_still_a_tuple()
    {
        var (type, de) = ParseType("(int, string)");
        Assert.False(de.HasErrors);
        Assert.Equal(2, Assert.IsType<TupleType>(type).Elements.Length);
    }

    [Fact]
    public void A_trailing_comma_is_not_a_one_tuple() =>
        // '(T,)' means a tuple, and its second element is missing. Without this distinction the grouping
        // would be a silent reinterpretation of what someone wrote.
        Assert.Contains(ParseType("(int,)").De.Diagnostics, d => d.Code == "LYR-PAR0010");

    [Fact]
    public void The_ast_dumper_handles_a_static_binding()
    {
        // 'lyrc parse' crashed here: the dumper did not know StaticBindingDecl although check and lower
        // handle the construct. A debug command that throws on valid code is exactly the kind of gap no
        // gate measures.
        var sm = new SourceManager();
        var id = sm.AddVirtual("t.lyr", "struct V { x: int, static let ZERO: int = 0; }");
        var de = new DiagnosticEngine(sm);
        var module = new Parser(sm, id, de).ParseModule();

        var dump = AstDumper.Dump(module, sm);

        Assert.Contains("StaticLet", dump);
        Assert.Contains("Let ZERO", dump);
    }

    [Fact]
    public void Empty_parentheses_are_not_a_type() =>
        Assert.Contains(ParseType("()").De.Diagnostics, d => d.Code == "LYR-PAR0011");
}
