using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;

namespace Lyric.Tests.Parsing;

/// <summary>
/// Runde Klammern in Typ-Position (Sprache.md §4).
///
/// <para>Sie sind noetig, weil <c>fn(A) -&gt; R</c> als einziger Typ der Sprache nach rechts offen
/// ist: <c>fn(int) -&gt; void[]</c> ist eine Funktion, die <c>void[]</c> liefert, und ein Array von
/// Funktionswerten liess sich vorher <b>gar nicht hinschreiben</b> — der Typ existierte (ein
/// Array-Literal von Lambdas hat ihn), er war nur nicht benennbar.</para>
///
/// <para>Ein Konflikt mit Tupeln entsteht nicht: <c>TupleType</c> verlangt seit jeher Aritaet 2,
/// der Platz fuer <c>(T)</c> war frei. Rust braucht dafuer <c>(T,)</c>.</para>
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
        // Kein TupleType mit einem Element — der innere Knoten wandert unveraendert nach oben.
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
        // Der Fall, fuer den es die Klammerung gibt.
        var (type, de) = ParseType("(fn(int) -> void)[]");
        Assert.False(de.HasErrors);

        var array = Assert.IsType<ArrayType>(type);
        Assert.IsType<FunctionType>(array.Element);
    }

    [Fact]
    public void Without_parentheses_the_bracket_belongs_to_the_return_type()
    {
        // Die Praezedenz bleibt, wie sie war — sie umzudrehen haette 'fn(): int[]' still zu etwas
        // anderem gemacht als bisher.
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
        // '(T,)' hiess „Tupel", und dafuer fehlt das zweite Element. Ohne diese Unterscheidung
        // waere die Klammerung eine stille Umdeutung dessen, was jemand geschrieben hat.
        Assert.Contains(ParseType("(int,)").De.Diagnostics, d => d.Code == "LYR-PAR0010");

    [Fact]
    public void Empty_parentheses_are_not_a_type() =>
        Assert.Contains(ParseType("()").De.Diagnostics, d => d.Code == "LYR-PAR0011");
}
