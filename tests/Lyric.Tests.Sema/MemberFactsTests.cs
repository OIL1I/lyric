using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// Which members a type offers, and that the answer agrees with the type checker's.
///
/// <para>The enumeration and the lookup are separate code walking the same three sources. The last
/// test here is what holds them together: every member the enumeration offers is accessed in a real
/// program, and none of them may come back as <c>LYR-SEM0012</c>, "has no member". Without it the two
/// can drift, and the direction that hurts is silent — a list that offers something uncallable.</para>
/// </summary>
public class MemberFactsTests
{
    /// <summary>A program with all four sources of a member: a field, an instance method, an
    /// extension and an interface default.</summary>
    private const string Program = """
        interface Greets {
            fn name(): int;
            fn greet(): int { return this.name() + 1; }
        }

        struct Point :: [Greets] {
            x: int,
            fn doubled(): int { return this.x * 2; },
            static fn origin(): int { return 0; },
            fn name(): int { return this.x; }
        }

        extend Point {
            fn tripled(): int { return this.x * 3; }
        }

        fn main(): int { return 0; }
        """;

    private sealed record Checked(Compilation Compilation, BindingResult Binding, DiagnosticEngine Diagnostics);

    private static Checked Analyze(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        Semantics.Analyze(comp, binding, de);
        return new Checked(comp, binding, de);
    }

    private static TypeSymbol TypeNamed(Compilation comp, string name) =>
        Assert.IsType<TypeSymbol>(comp.Modules[0].Members.LookupLocal(name));

    private static string[] Instance(string source, string type)
    {
        var checkedProgram = Analyze(source);
        var ts = TypeNamed(checkedProgram.Compilation, type);

        return MemberFacts
            .OfInstance(checkedProgram.Compilation, checkedProgram.Binding, ts, checkedProgram.Compilation.Modules[0])
            .Select(c => c.Symbol.Name)
            .ToArray();
    }

    // ------------------------------------------------------------------ the sources

    [Fact]
    public void The_instance_side_gathers_all_four_sources()
    {
        var members = Instance(Program, "Point");

        Assert.Contains("x", members);          // field
        Assert.Contains("doubled", members);    // instance method
        Assert.Contains("tripled", members);    // extension
        Assert.Contains("greet", members);      // interface default
    }

    [Fact]
    public void The_instance_side_leaves_out_what_belongs_to_the_type()
    {
        Assert.DoesNotContain("origin", Instance(Program, "Point"));
    }

    [Fact]
    public void The_static_side_offers_only_what_belongs_to_the_type()
    {
        var checkedProgram = Analyze(Program);
        var members = MemberFacts
            .OfType(checkedProgram.Compilation, TypeNamed(checkedProgram.Compilation, "Point"),
                checkedProgram.Compilation.Modules[0])
            .Select(c => c.Symbol.Name)
            .ToArray();

        Assert.Contains("origin", members);
        Assert.DoesNotContain("x", members);
        Assert.DoesNotContain("doubled", members);
    }

    [Fact]
    public void An_abstract_interface_member_is_not_offered_twice()
    {
        // 'name' is declared by the interface without a body and implemented by the type. It is the
        // type's member; offering the interface's declaration beside it would be the same name twice.
        var members = Instance(Program, "Point");
        Assert.Single(members, m => m == "name");
    }

    [Fact]
    public void A_source_is_reported_with_each_member()
    {
        var checkedProgram = Analyze(Program);
        var candidates = MemberFacts
            .OfInstance(checkedProgram.Compilation, checkedProgram.Binding,
                TypeNamed(checkedProgram.Compilation, "Point"), checkedProgram.Compilation.Modules[0])
            .ToDictionary(c => c.Symbol.Name, c => c.Source);

        Assert.Equal(MemberSource.Own, candidates["x"]);
        Assert.Equal(MemberSource.Extension, candidates["tripled"]);
        Assert.Equal(MemberSource.InterfaceDefault, candidates["greet"]);
    }

    // ------------------------------------------------------------------ the two must agree

    [Fact]
    public void Every_offered_member_can_actually_be_reached()
    {
        // The enumeration is separate code from the lookup. This is what keeps them together: each
        // offered name is written into a real program and the type checker must not answer that the
        // type has no such member.
        var offered = Instance(Program, "Point");

        // An empty list would satisfy the loop below without checking anything. Five: the field, the
        // instance method, the extension, the interface default, and the type's own implementation
        // of the interface's abstract member.
        Assert.Equal(
            ["doubled", "greet", "name", "tripled", "x"],
            offered.OrderBy(m => m, StringComparer.Ordinal).ToArray());

        foreach (var member in offered)
        {
            var source = Program.Replace(
                "fn main(): int { return 0; }",
                $"fn main(): int {{\n    let p = Point {{ x = 1 }};\n    let m = p.{member};\n    return 0;\n}}");

            var de = Analyze(source).Diagnostics;

            Assert.DoesNotContain(de.Diagnostics, d =>
                d.Code == "LYR-SEM0012"
                && d.Message.Contains($"'{member}'", StringComparison.Ordinal));
        }
    }
}
