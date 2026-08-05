using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// <b>Nichts wird stillschweigend ungeprüft durchgelassen.</b>
///
/// <para>Diese Tests schützen eine Invariante, keine einzelne Regel: <see cref="ErrorType"/>
/// bedeutet <i>ausschließlich</i> „hierfür wurde bereits eine Diagnose gemeldet". Jeder Konsument
/// verlässt sich darauf und schweigt bei <c>Error</c> — wer den Typ auch für „ich weiß nicht, was
/// das ist" benutzt, macht daraus ein stummes Durchwinken.</para>
///
/// <para>Genau das war der Zustand: sechs Konstrukte prüften vollständig durch, obwohl sie ungültig
/// sind, und rissen jede Verwendung mit. Der schlimmste Fall war ein Tippfehler im Modulnamen —
/// <c>import std.io.consle</c> — der die Prüfung jeder Nutzung des Imports abschaltete.</para>
///
/// <para>Jede Fixture hier enthält deshalb <b>zusätzlich</b> einen groben Folgefehler
/// (<c>.quatsch</c>, falsche Arität). Würde die Prüfung stumm abbrechen, meldete der Compiler gar
/// nichts — <see cref="Assert.True(bool, string)"/> auf <c>HasErrors</c> ist die eigentliche
/// Aussage, der Code-Vergleich nur die Präzisierung.</para>
/// </summary>
public class SilentSkipTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    private static void AssertReports(string source, string code)
    {
        var de = Check(source);
        Assert.True(de.HasErrors,
            "this source is invalid but was accepted without any diagnostic — " +
            "something was silently skipped:\n" + source);
        Assert.Contains(de.Diagnostics, d => d.Code == code);
    }

    // --- Ein Typ- oder Modulname in Wert-Position ---

    [Theory]
    // Sieht aus wie ein Konstruktor, ist keiner: Lyric konstruiert über 'P { … }'. Vorher lieferte
    // das Error, und Arität, Argumenttypen und '.quatsch' blieben allesamt ungeprüft.
    [InlineData("class P { hp: int }\nfn main(): int { return P(1, 2, 3).quatsch; }")]
    [InlineData("class P { hp: int }\nfn main(): int { let x = P; return x.quatsch; }")]
    [InlineData("import std.io.console;\nfn main(): int { let x = console; return x.quatsch; }")]
    public void A_type_or_module_in_value_position_is_reported(string source) =>
        AssertReports(source, "LYR-SEM0052");

    /// <summary>Die Gegenprobe — <b>ohne die wäre der Fix eine Verschlechterung</b>: als
    /// Member-Ziel ist ein Typ- oder Modulname legitim, und beide Formen müssen weiter durchgehen.
    /// Genau daran scheitert der naheliegende Fix „melde es einfach am Erzeuger".</summary>
    [Theory]
    [InlineData("class P {\n  hp: int,\n  static fn make(): P { return P { hp = 1 }; }\n}\nfn main(): int { return P.make().hp; }")]
    [InlineData("import std.io.console;\nfn main(): int { console.println(\"hi\"); return 0; }")]
    public void A_type_or_module_as_a_member_target_stays_legal(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            "member access on a type/module name must stay legal, but got:\n" +
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    // --- Unauffindbare Module ---

    /// <summary>
    /// Der teuerste Fall. Ein fehlendes 'o' in <c>consle</c> galt als „extern/opak" — eine Regel
    /// aus M3, als es den <c>ModuleLoader</c> noch nicht gab. Der Import blieb stumm, und beide
    /// <c>println</c>-Aufrufe darunter ebenfalls, obwohl der eine den falschen Argumenttyp und der
    /// andere die falsche Arität hat.
    /// </summary>
    [Fact]
    public void A_typo_in_a_module_path_is_reported() =>
        AssertReports(
            """
            import std.io.consle { println };
            fn main(): int { println(42); println("a", "b"); return 0; }
            """,
            "LYR-RES0003");

    [Theory]
    [InlineData("import gibts.nicht;\nfn main(): int { return 0; }")]
    [InlineData("import gibts.nicht { thing };\nfn main(): int { return thing(1, 2, 3).quatsch; }")]
    [InlineData("import gibts.nicht { Thing };\nfn f(t: Thing): int { return t.quatsch; }\nfn main(): int { return 0; }")]
    public void An_unresolvable_module_is_reported(string source) =>
        AssertReports(source, "LYR-RES0003");

    /// <summary>Ein unbekanntes <b>Symbol</b> in einem bekannten Modul wurde schon immer gemeldet.
    /// Der Test hält die Asymmetrie fest, die es vorher gab: Symbol ja, Modul nein.</summary>
    [Fact]
    public void An_unknown_symbol_in_a_known_module_is_reported() =>
        AssertReports("import std.io.console { printlnn };\nfn main(): int { return 0; }",
            "LYR-RES0004");

    // --- Attribute ---

    [Fact]
    public void An_attribute_is_not_an_expression() =>
        AssertReports("fn main(): int { let x = @test; return x.quatsch; }", "LYR-SEM0053");

    // --- Kontrolle ---

    /// <summary>Zeigt, dass der Harness überhaupt Fehler sieht — sonst könnten alle Tests oben aus
    /// dem falschen Grund grün sein.</summary>
    [Fact]
    public void The_harness_reports_an_ordinary_type_error() =>
        AssertReports("fn main(): int { let s: string = 1; return 0; }", "LYR-SEM0001");
}
