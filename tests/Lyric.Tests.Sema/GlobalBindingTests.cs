using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Modul-Bindungen sind unveränderlich — ADR-025.
///
/// <para><b>Beide Seiten stehen hier</b>, und das ist der Zweck dieser Datei: die Regel verbietet
/// die Neubindung des <i>Namens</i>, nicht veränderlichen globalen Zustand. Ein Test, der nur das
/// Verbot festhielte, ließe offen, wie weit es reicht — und genau diese Unklarheit hat bei ADR-020
/// und ADR-023 je eine Regel überleben lassen, die niemand mehr begründen konnte.</para>
///
/// <para>Die Regel galt seit P5b und stand bis 2026-08-09 nur als Klammerkommentar in der
/// Grammatik und als Parser-Meldung — ohne Begründung an einer Stelle, an der jemand sie
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
        // ADR-020: 'let' bindet den Namen, nicht den Inhalt. Ohne diesen Test läse sich ADR-025
        // wie ein Verbot von globalem Zustand, und das ist es nicht.
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
        // Das Einzige, was die Regel wirklich verhindert.
        Assert.Contains(Check("""
            let n = 0;
            fn main(): int { n = 5; return n; }
            """).Diagnostics, d => d.Code == "LYR-SEM0019");
}
