using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// A field written twice in one initializer.
///
/// <para><c>P { x = 1, x = 2 }</c> names a field twice, and only one value can land in it. The
/// lowering writes fields by name and refuses a second write, so without this diagnostic the input
/// was not an error message but a compiler crash.</para>
/// </summary>
public class DuplicateInitFieldTests
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
    public void A_field_written_twice_in_a_struct_initializer_is_reported()
    {
        var de = Check("struct P { x: int }\nfn main(): int { let p = P { x = 1, x = 2 }; return p.x; }");
        Assert.Contains("LYR-SEM0070", Codes(de));
    }

    [Fact]
    public void A_field_written_twice_in_a_class_initializer_is_reported()
    {
        var de = Check("class C { x: int }\nfn main(): int { let c = C { x = 1, x = 2 }; return c.x; }");
        Assert.Contains("LYR-SEM0070", Codes(de));
    }

    [Fact]
    public void A_field_written_twice_in_a_struct_variant_initializer_is_reported()
    {
        var de = Check("enum Shape { Tri { a: int, b: int }, Empty; }\n"
                       + "fn main(): int { let s = Shape.Tri { a = 1, a = 2, b = 3 }; return 0; }");
        Assert.Contains("LYR-SEM0070", Codes(de));
    }

    [Fact]
    public void The_message_names_the_field()
    {
        var de = Check("struct P { x: int }\nfn main(): int { let p = P { x = 1, x = 2 }; return p.x; }");
        var dup = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0070");
        Assert.Contains("x", dup.Message);
    }

    [Fact]
    public void A_field_written_three_times_is_one_fault_per_repeat()
    {
        // The first write is fine; each repeat is its own diagnostic at its own span.
        var de = Check("struct P { x: int }\nfn main(): int { let p = P { x = 1, x = 2, x = 3 }; return p.x; }");
        Assert.Equal(2, Codes(de).Count(c => c == "LYR-SEM0070"));
    }

    // ------------------------------------------------------------------ counter-checks

    [Fact]
    public void Distinct_fields_are_accepted()
    {
        var de = Check("struct P { x: int, y: int }\n"
                       + "fn main(): int { let p = P { x = 1, y = 2 }; return p.x + p.y; }");
        Assert.Empty(Codes(de));
    }

    [Fact]
    public void The_same_field_in_two_separate_initializers_is_not_a_repeat()
    {
        // The seen-set must be per initializer, not per checker.
        var de = Check("struct P { x: int }\n"
                       + "fn main(): int { let a = P { x = 1 }; let b = P { x = 2 }; return a.x + b.x; }");
        Assert.Empty(Codes(de));
    }

    [Fact]
    public void Distinct_fields_in_a_struct_variant_initializer_are_accepted()
    {
        var de = Check("enum Shape { Tri { a: int, b: int }, Empty; }\n"
                       + "fn main(): int { let s = Shape.Tri { a = 1, b = 2 }; return 0; }");
        Assert.Empty(Codes(de));
    }
}
