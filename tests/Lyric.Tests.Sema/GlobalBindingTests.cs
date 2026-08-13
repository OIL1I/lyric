using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Module bindings are immutable.
///
/// <para>BOTH SIDES STAND HERE, and that is the purpose of this file: the rule forbids rebinding the
/// NAME rather than mutable global state. A test holding only the prohibition would leave open how far
/// it reaches — and exactly that vagueness let a rule nobody could justify survive twice before.</para>
///
/// <para>The rule stood only as a parenthesised comment in the grammar and as a parser message, without
/// a reason at a place where someone would
/// findet.</para>
/// </summary>
public class GlobalBindingTests
{
    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    private static void Allowed(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            string.Join(" | ", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    [Fact]
    public void A_module_level_var_is_rejected() =>
        Assert.Contains(Check("var n = 0;\nfn main(): int { return 0; }").Diagnostics,
            d => d.Code == "LYR-PAR0027");

    [Fact]
    public void A_module_level_let_is_fine() =>
        Allowed("let n = 0;\nfn main(): int { return n; }");

    [Fact]
    public void The_content_of_a_global_array_stays_mutable() =>
        // A 'let' binds the name rather than the content. Without this test the rule would read as a
        // prohibition of global state, and it is not that.
        Allowed("""
            let zahlen = [1, 2, 3];
            fn main(): int { zahlen[0] = 99; return zahlen[0]; }
            """);

    [Fact]
    public void The_fields_of_a_global_object_stay_mutable() =>
        Allowed("""
            class Zaehler { stand: int = 0, }
            let z = Zaehler { };
            fn main(): int { z.stand = 42; return z.stand; }
            """);

    [Fact]
    public void The_name_itself_cannot_be_rebound() =>
        // The only thing the rule really prevents.
        Assert.Contains(Check("""
            let n = 0;
            fn main(): int { n = 5; return n; }
            """).Diagnostics, d => d.Code == "LYR-SEM0019");
}
