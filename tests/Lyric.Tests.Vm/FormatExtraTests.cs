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
/// `std.fmt` — Zahlenbasen, Ausrichtung, Tabellen (M8b/S7), und der `uint`-Wandler.
///
/// <para>Alles außer `formatUint` ist in Lyric: eine Basis-Umwandlung ist Division mit Rest,
/// Ausrichtung ist Auffüllen. Beides kann die Sprache seit ADR-022 selbst.</para>
///
/// <para>Der eigentliche Anlass für diesen Slice steht unter „vorzeichenlos": <c>f"{u}"</c> mit
/// einem großen <c>uint</c> lieferte <b>-1</b>. Kein Absturz, eine falsche Ausgabe — die Sorte
/// Fehler, die niemand meldet, weil sie wie ein eigener Rechenfehler aussieht.</para>
/// </summary>
public class FormatExtraTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Out(string body)
    {
        var source = "import std.io.console { println };\n" + body;
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

        var writer = new StringWriter();
        de.RenderText(writer);
        Assert.False(de.HasErrors, "source did not compile: " + writer);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        var output = new StringWriter();
        Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(output, TextWriter.Null));
        return output.ToString().Trim();
    }

    private const string Head = """
        import std.fmt { formatHex, formatBinary, formatOctal, formatRadix, formatHexPadded,
                         padLeft, padRight, center, formatBytes, table };

        """;

    // ------------------------------------------------------------ vorzeichenlos

    /// <summary>
    /// Ein großes <c>uint</c> wird als solches gedruckt, nicht als negative Zahl.
    /// </summary>
    /// <remarks>Bis M8b/S7 ging jede Ganzzahl durch <c>fromInt</c>, und ein <c>uint</c> jenseits
    /// von <c>int64.MaxValue</c> erschien als <c>-1</c>. Der Fehler stand seit dem
    /// f-String-Slice als bekannte Grenze im Code — behoben durch einen eigenen Wandler und die
    /// Unterscheidung nach Vorzeichen im Lowering.</remarks>
    [Fact]
    public void A_large_uint_prints_unsigned() =>
        Assert.Equal("18446744073709551615", Out("""
            fn main(): int {
                let u: uint = 18446744073709551615;
                println(f"{u}");
                return 0;
            }
            """));

    [Fact]
    public void A_large_uint_survives_a_format_spec() =>
        // Der zweite Weg: mit Spec ueber std.fmt statt std.string. Zwei Pfade, derselbe Fehler.
        Assert.Equal("18,446,744,073,709,551,615", Out("""
            fn main(): int {
                let u: uint = 18446744073709551615;
                println(f"{u:N0}");
                return 0;
            }
            """));

    [Theory]
    [InlineData("uint8", "200", "200")]
    [InlineData("uint16", "60000", "60000")]
    [InlineData("uint32", "4000000000", "4000000000")]
    public void Narrow_unsigned_types_widen_without_sign(string type, string literal, string expected) =>
        // Ein uint8 muss zu u64 verbreitert werden und nicht zu i64: der Zwischenschritt ueber
        // einen vorzeichenbehafteten Typ deutete das oberste Bit um.
        Assert.Equal(expected, Out($$"""
            fn main(): int {
                let x: {{type}} = {{literal}};
                println(f"{x}");
                return 0;
            }
            """));

    [Fact]
    public void Signed_types_are_untouched() =>
        // Die Gegenprobe: negative Zahlen muessen negativ bleiben.
        Assert.Equal("-42", Out("""
            fn main(): int {
                let n: int = -42;
                println(f"{n}");
                return 0;
            }
            """));

    // ------------------------------------------------------------ Zahlenbasen

    [Theory]
    [InlineData("formatHex(255)", "ff")]
    [InlineData("formatHex(0)", "0")]
    [InlineData("formatHex(0 - 255)", "-ff")]
    [InlineData("formatBinary(10)", "1010")]
    [InlineData("formatOctal(64)", "100")]
    [InlineData("formatRadix(35, 36)", "z")]
    [InlineData("formatRadix(255, 16)", "ff")]
    [InlineData("formatRadix(5, 1)", "")]          // ungueltige Basis
    [InlineData("formatRadix(5, 37)", "")]
    [InlineData("formatHexPadded(255, 4)", "00ff")]
    [InlineData("formatHexPadded(65535, 2)", "ffff")]   // laenger als width: nicht abschneiden
    public void Radix_formatting_is_exact(string expression, string expected) =>
        Assert.Equal(expected, Out(Head + $"fn main(): int {{ println({expression}); return 0; }}"));

    [Fact]
    public void A_negative_number_keeps_its_digits_positive() =>
        // '%' folgt in Lyric dem Vorzeichen des Dividenden — ohne die Betragsbildung VOR der
        // Schleife kaemen negative Ziffern heraus.
        Assert.Equal("-1010", Out(Head +
            "fn main(): int { println(formatBinary(0 - 10)); return 0; }"));

    // ------------------------------------------------------------ Ausrichtung

    [Theory]
    [InlineData("padLeft(\"x\", 4)", "[   x]")]
    [InlineData("padRight(\"x\", 4)", "[x   ]")]
    [InlineData("center(\"x\", 5)", "[  x  ]")]
    [InlineData("center(\"x\", 4)", "[ x  ]")]      // Rest nach rechts
    [InlineData("center(\"lang\", 2)", "[lang]")]   // breiter als width
    public void Alignment_pads_as_documented(string expression, string expected) =>
        // Die Klammern sind noetig, nicht Zierde: 'Out' trimmt die Ausgabe, und genau die
        // Leerzeichen an den Raendern sind hier das Ergebnis.
        Assert.Equal(expected, Out(Head +
            $"fn main(): int {{ println(\"[\" + {expression} + \"]\"); return 0; }}"));

    // ------------------------------------------------------------ Groessen

    [Theory]
    [InlineData("512", "512 B")]
    [InlineData("1023", "1023 B")]
    [InlineData("1024", "1.0 KB")]
    [InlineData("1536", "1.5 KB")]
    [InlineData("1048576", "1.0 MB")]
    public void Byte_sizes_step_at_1024(string bytes, string expected) =>
        Assert.Equal(expected, Out(Head + $"fn main(): int {{ println(formatBytes({bytes})); return 0; }}"));

    // ------------------------------------------------------------ Tabellen

    [Fact]
    public void A_table_aligns_its_columns() =>
        Assert.Equal("a   | bb\nccc | d", Out(Head + """
            fn main(): int {
                println(table([["a", "bb"], ["ccc", "d"]], " | "));
                return 0;
            }
            """));

    [Fact]
    public void The_last_column_is_not_padded() =>
        // Sonst haengen unsichtbare Leerzeichen am Zeilenende, und die sieht man erst im Diff.
        Assert.Equal("a | bb\nb | c", Out(Head + """
            fn main(): int {
                println(table([["a", "bb"], ["b", "c"]], " | "));
                return 0;
            }
            """));

    [Fact]
    public void A_short_row_gets_empty_cells() =>
        Assert.Equal("a | b | c\nd |   |", Out(Head + """
            fn main(): int {
                println(table([["a", "b", "c"], ["d"]], " | "));
                return 0;
            }
            """));
}
