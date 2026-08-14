using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// A type alias that is defined through itself.
///
/// <para>An alias names a type rather than being one, so expanding it is a recursion with no base
/// case of its own. Without a guard <c>type A = B; type B = A;</c> was not a diagnostic but a STACK
/// OVERFLOW — and .NET cannot catch one, so the compiler process died rather than the compilation
/// failing. That is why these tests exist at all: a crash inside the type checker takes the test
/// runner with it.</para>
/// </summary>
public class TypeAliasCycleTests
{
    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        new TypeChecker(comp, binding, de).Check();
        return de;
    }

    private static string[] Codes(DiagnosticEngine de) =>
        de.Diagnostics.Select(d => d.Code).ToArray();

    [Fact]
    public void An_alias_defined_through_itself_is_reported()
    {
        var de = Check("type A = A;\nfn f(x: A): int { return 0; }\nfn main(): int { return 0; }");
        Assert.Contains("LYR-SEM0064", Codes(de));
    }

    [Fact]
    public void A_cycle_over_two_aliases_is_reported()
    {
        var de = Check("type A = B;\ntype B = A;\nfn f(x: A): int { return 0; }\nfn main(): int { return 0; }");
        Assert.Contains("LYR-SEM0064", Codes(de));
    }

    [Fact]
    public void A_cycle_over_three_aliases_is_reported()
    {
        var de = Check("type A = B;\ntype B = C;\ntype C = A;\nfn f(x: A): int { return 0; }\n"
                       + "fn main(): int { return 0; }");
        Assert.Contains("LYR-SEM0064", Codes(de));
    }

    [Fact]
    public void A_cycle_through_a_type_argument_is_reported()
    {
        // The alias does not stand alone but inside another type, so the guard has to hold on the
        // way down as well.
        var de = Check("type A = A[];\nfn f(x: A): int { return 0; }\nfn main(): int { return 0; }");
        Assert.Contains("LYR-SEM0064", Codes(de));
    }

    [Fact]
    public void A_cycle_is_reported_once_per_alias_and_not_once_per_use()
    {
        // The cycle is a property of the declaration. One message per use site would be the same
        // fault repeated, and every use is a use.
        var de = Check("type A = A;\nfn f(x: A): int { return 0; }\nfn g(y: A): int { return 0; }\n"
                       + "fn h(): A { return 0; }\nfn main(): int { return 0; }");
        Assert.Single(Codes(de), c => c == "LYR-SEM0064");
    }

    [Fact]
    public void The_message_names_the_alias()
    {
        var de = Check("type Loop = Loop;\nfn f(x: Loop): int { return 0; }\nfn main(): int { return 0; }");
        var cycle = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0064");
        Assert.Contains("Loop", cycle.Message);
    }

    // ------------------------------------------------------------------ counter-checks

    [Fact]
    public void A_chain_of_aliases_without_a_cycle_is_accepted()
    {
        // Without this, a guard that rejected every nested alias would pass every test above.
        var de = Check("type A = int;\ntype B = A;\ntype C = B;\nfn f(x: C): int { return x; }\n"
                       + "fn main(): int { return f(1); }");
        Assert.Empty(Codes(de));
    }

    [Fact]
    public void The_same_alias_used_twice_in_one_signature_is_not_a_cycle()
    {
        // Two uses of one alias are not a cycle; the guard must unwind between them rather than
        // treating the second as a repeat visit.
        var de = Check("type Id = int;\nfn f(a: Id, b: Id): Id { return a + b; }\n"
                       + "fn main(): int { return f(1, 2); }");
        Assert.Empty(Codes(de));
    }

    [Fact]
    public void An_alias_naming_a_generic_instance_is_not_a_cycle()
    {
        var de = Check("struct Box<T> { v: T, }\ntype IntBox = Box<int>;\n"
                       + "fn f(b: IntBox): int { return b.v; }\n"
                       + "fn main(): int { return f(Box<int> { v = 3 }); }");
        Assert.Empty(Codes(de));
    }
}
