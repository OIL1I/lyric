using System.Runtime.CompilerServices;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// `std.string` als richtiger Typ (M8/S2).
///
/// <para><b>Die eine Regel, aus der alles folgt</b>: `Sprache.md` §4 sagt „`char` = ein Unicode-
/// Codepoint". Also zählen Länge, Positionen und Iteration <b>Codepoints</b> — sonst zählte die
/// Länge etwas anderes, als die Iteration liefert, und das Typsystem widerspräche sich selbst.
/// C#, Java und JavaScript haben genau diesen Widerspruch: dort ist ein `char` eine
/// UTF-16-Einheit, und die Länge eines Emoji ist 2.</para>
///
/// <para><b>Der teuerste Test der Datei ist deshalb der mit dem Emoji.</b> Alle anderen bleiben
/// auch grün, wenn die Implementierung heimlich UTF-16-Einheiten zählt — ASCII macht keinen
/// Unterschied. Erst ein Zeichen außerhalb der BMP trennt die beiden Modelle.</para>
/// </summary>
public class StringTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static (long Exit, string Out) Run(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        var diagnostics = new StringWriter();
        de.RenderText(diagnostics);
        Assert.False(de.HasErrors, "source did not compile: " + diagnostics);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        var output = new StringWriter();
        var exit = Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(output, TextWriter.Null)).AsI64;
        return (exit, output.ToString().ReplaceLineEndings("\n"));
    }

    private static long Eval(string imports, string body) =>
        Run($"import std.string {{ {imports} }};\nfn main(): int {{ {body} }}").Exit;

    // ------------------------------------------------------------------ Codepoints

    [Fact]
    public void Length_counts_codepoints_not_utf16_units() =>
        // DER Test. "a😀b" sind drei Codepoints und vier UTF-16-Einheiten; C# sagt hier 4.
        // Ohne diesen Fall wäre jede andere Zusicherung dieser Datei mit beiden Modellen
        // vereinbar.
        Assert.Equal(3, Eval("length", "return length(\"a\\u{1F600}b\");"));

    [Fact]
    public void An_astral_codepoint_is_one_character() =>
        // Die Ergänzung zur Länge: auch die Position stimmt. Zählte charAt UTF-16-Einheiten,
        // käme hier ein halbes Surrogate Pair heraus — ein Zeichen ohne Partner, das beim
        // Ausgeben zu U+FFFD wird. Gemessen wird über die Ausgabe, weil 'char as int' in Lyric
        // kein erlaubter Cast ist (§6.5).
        Assert.Equal("\U0001F600\n", Run("""
            import std.string { charAt };
            import std.io.console { println };
            fn main(): int { println(f"{charAt("a\u{1F600}b", 1)}"); return 0; }
            """).Out);

    [Fact]
    public void Iteration_visits_codepoints() =>
        Assert.Equal(3, Eval("length", """
            var n = 0;
            for (c in "a\u{1F600}b") { n = n + 1; }
            return n;
            """));

    [Fact]
    public void Substring_cuts_at_codepoint_boundaries() =>
        // Schnitte mitten durch ein Surrogate Pair würden hier eine kaputte Zeichenkette
        // liefern; gemessen wird über die Länge des Ergebnisses, weil die stabil vergleichbar ist.
        Assert.Equal(2, Eval("length, substring", "return length(substring(\"a\\u{1F600}b\", 1, 2));"));

    [Fact]
    public void IndexOf_returns_a_codepoint_position() =>
        // Der Rückgabewert muss als Argument für charAt und substring taugen — bei einer
        // Position in UTF-16-Einheiten oder Bytes täte er das nicht.
        Assert.Equal(2, Eval("indexOf", "return indexOf(\"a\\u{1F600}b\", \"b\");"));

    // ------------------------------------------------------------------ die üblichen Fälle

    [Fact]
    public void Basic_queries_work() =>
        Assert.Equal(10, Eval("length", "return length(\"Hallo Welt\");"));

    [Fact]
    public void Substring_takes_a_start_and_a_count() =>
        Assert.Equal(0, Run("""
            import std.string { substring };
            import std.io.console { println };
            fn main(): int { println(substring("Hallo Welt", 6, 4)); return 0; }
            """).Exit);

    [Fact]
    public void Substring_produces_the_expected_text() =>
        Assert.Equal("Welt\n", Run("""
            import std.string { substring };
            import std.io.console { println };
            fn main(): int { println(substring("Hallo Welt", 6, 4)); return 0; }
            """).Out);

    [Fact]
    public void IndexOf_reports_minus_one_when_absent() =>
        Assert.Equal(-1, Eval("indexOf", "return indexOf(\"abc\", \"z\");"));

    [Fact]
    public void Split_returns_the_parts() =>
        Assert.Equal(3, Eval("split", "return split(\"a,b,c\", \",\").length;"));

    [Fact]
    public void Split_keeps_the_parts_in_order() =>
        Assert.Equal("b\n", Run("""
            import std.string { split };
            import std.io.console { println };
            fn main(): int { println(split("a,b,c", ",")[1]); return 0; }
            """).Out);

    [Fact]
    public void Case_conversion_is_ordinal_not_cultural() =>
        // Ordinal heißt: dasselbe Programm liefert auf jeder Maschine dasselbe. Unter einer
        // türkischen Locale würde eine kulturabhängige Umwandlung aus 'i' ein 'İ' machen — der
        // Klassiker, der Software nur auf manchen Rechnern kaputtmacht.
        Assert.Equal("TITLE\n", Run("""
            import std.string { toUpper };
            import std.io.console { println };
            fn main(): int { println(toUpper("title")); return 0; }
            """).Out);

    [Fact]
    public void Trim_removes_surrounding_whitespace() =>
        Assert.Equal("x\n", Run("""
            import std.string { trim };
            import std.io.console { println };
            fn main(): int { println(trim("   x   ")); return 0; }
            """).Out);

    [Fact]
    public void Predicates_answer_both_ways()
    {
        Assert.Equal(1, Eval("startsWith", "if (startsWith(\"abc\", \"ab\")) { return 1; } return 0;"));
        Assert.Equal(0, Eval("startsWith", "if (startsWith(\"abc\", \"bc\")) { return 1; } return 0;"));
        Assert.Equal(1, Eval("endsWith", "if (endsWith(\"abc\", \"bc\")) { return 1; } return 0;"));
        Assert.Equal(0, Eval("contains", "if (contains(\"abc\", \"z\")) { return 1; } return 0;"));
    }

    // ------------------------------------------------------------------ was NICHT geht

    [Fact]
    public void A_string_cannot_be_indexed()
    {
        // Bewusst keine Sprachlücke, sondern eine Entscheidung: eine Codepoint-Position kostet
        // O(n), also wäre 'for (i…) s[i]' quadratisch, ohne dass man es der Schleife ansieht.
        // Rust verbietet die Indizierung aus demselben Grund.
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "fn main(): int { let s = \"abc\"; let c = s[0]; return 0; }");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);

        var reported = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0007");

        // Die Meldung muss den Ausweg nennen. Eine, die nur „not indexable" sagt, ließe den
        // Nutzer glauben, es fehle ein Feature — statt dass es eine Entscheidung war.
        Assert.Contains("charAt", reported.Message);
        Assert.Contains("for (c in s)", reported.Message);
    }

    [Fact]
    public void An_array_can_still_be_indexed() =>
        // Die Gegenprobe: der Fix darf nicht die Array-Indizierung mitgenommen haben.
        Assert.Equal(2, Run("fn main(): int { let xs = [1, 2, 3]; return xs[1]; }").Exit);
}
