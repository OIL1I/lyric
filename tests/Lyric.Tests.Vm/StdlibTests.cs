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
/// `std.math`, `std.os` und `std.io.file` (M8/S7).
///
/// <para><b>Fehler sind Rückgabewerte, keine Exceptions.</b> Eine Datei, die nicht existiert, und
/// eine Umgebungsvariable, die nicht gesetzt ist, sind gewöhnliche Zustände der Welt — kein
/// `panic` und keine Exception. Beide liefern `?T`. Ein `panic` bleibt dem vorbehalten, was der
/// Programmierer falsch gemacht hat (ein Index daneben), eine Exception dem, was ein Aufrufer
/// sinnvoll behandeln kann.</para>
///
/// <para>Die Dateitests schreiben in ein eigenes Temp-Verzeichnis und räumen auf. Sie sind
/// bewusst echte I/O und keine Attrappe: `std.io.file` ist die Grenze zum Host, und eine Attrappe
/// prüfte nur, ob der Compiler die Signatur lowert.</para>
/// </summary>
public class StdlibTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-s7-" + Guid.NewGuid().ToString("N")[..8]);

    public StdlibTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

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
        var exit = Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)), [],
            NativeRegistry.CreateDefault(output, TextWriter.Null)).AsI64;
        return (exit, output.ToString().ReplaceLineEndings("\n"));
    }

    // ------------------------------------------------------------------ std.math

    [Fact]
    public void Math_computes() =>
        Assert.Equal(9, Run("""
            import std.math { sqrt, floor, max };
            fn main(): int {
                return (sqrt(16.0) + floor(3.7) + max(1.0, 2.0)) as int;
            }
            """).Exit);

    [Fact]
    public void Pi_is_a_constant_from_a_stdlib_module() =>
        // `pub let pi` in einem NATIVEN Modul. Bis S7 wurden Globals der Stdlib nicht gesammelt —
        // die Begründung („native Module deklarieren nur Signaturen") galt für rumpflose `fn`,
        // aber ein `let` mit Initialisierer hat einen Wert. `shapes.lyr` hing genau daran.
        Assert.Equal(3, Run("""
            import std.math { pi };
            fn main(): int { return pi as int; }
            """).Exit);

    [Fact]
    public void Sqrt_of_a_negative_is_NaN_not_a_panic() =>
        // IEEE 754, wie `Sprache.md` §6.6 es für Fließkomma festlegt. Ein Fehlerfall wäre hier
        // eine Erfindung — die Hardware kennt keinen, und ein Programm, das ihn fängt, liefe auf
        // einer anderen Runtime anders. NaN ist mit sich selbst ungleich; genau daran wird es
        // gemessen.
        Assert.Equal(1, Run("""
            import std.math { sqrt };
            fn main(): int {
                let n = sqrt(0.0 - 1.0);
                if (n != n) { return 1; }
                return 0;
            }
            """).Exit);

    [Fact]
    public void Round_goes_to_even_at_a_half() =>
        // "round half to even": 2.5 -> 2, 3.5 -> 4. Konsequentes Aufrunden trüge über viele
        // Werte einen systematischen Fehler ein — deshalb macht es .NET so, und deshalb hier auch.
        Assert.Equal(6, Run("""
            import std.math { round };
            fn main(): int { return (round(2.5) + round(3.5)) as int; }
            """).Exit);

    // ------------------------------------------------------------------ std.os

    [Fact]
    public void Platform_names_the_system() =>
        Assert.Contains(Run("""
            import std.os { platform };
            import std.io.console { println };
            fn main(): int { println(platform()); return 0; }
            """).Out.Trim(), new[] { "windows", "linux", "macos", "unknown" });

    [Fact]
    public void An_unset_variable_is_null_not_an_error() =>
        // Ob eine Variable gesetzt ist, ist eine gewöhnliche Frage über die Umgebung — kein
        // `panic`, keine Exception.
        Assert.Equal(7, Run("""
            import std.os { env };
            import std.string { length };
            fn main(): int {
                let v = env("LYRIC_TEST_DEFINITELY_UNSET_XYZ");
                if (v == null) { return 7; }
                return 0;
            }
            """).Exit);

    // ------------------------------------------------------------------ std.io.file

    [Fact]
    public void A_file_round_trips()
    {
        var path = Path.Combine(_dir, "round.txt").Replace("\\", "\\\\");
        var result = Run($$"""
            import std.io.file { writeText, readText };
            import std.io.console { println };
            fn main(): int {
                let ok = writeText("{{path}}", "hallo");
                println(readText("{{path}}") ?? "<nichts>");
                return 0;
            }
            """);

        Assert.Equal("hallo\n", result.Out);
    }

    [Fact]
    public void Reading_a_missing_file_is_null_not_a_panic()
    {
        // DER Test der Fehler-Entscheidung. Eine Datei, die nicht existiert, ist ein Zustand der
        // Welt und kein Programmierfehler — hier trennt sich `?T` von `panic`.
        var path = Path.Combine(_dir, "gibtsnicht.txt").Replace("\\", "\\\\");
        Assert.Equal(5, Run($$"""
            import std.io.file { readText };
            fn main(): int {
                if (readText("{{path}}") == null) { return 5; }
                return 0;
            }
            """).Exit);
    }

    [Fact]
    public void Exists_and_remove_agree()
    {
        var path = Path.Combine(_dir, "weg.txt").Replace("\\", "\\\\");
        // Erst schreiben, dann prüfen, dann löschen, dann wieder prüfen — 1 und 0 an den
        // richtigen Stellen ergeben 10.
        Assert.Equal(10, Run($$"""
            import std.io.file { writeText, exists, remove };
            fn main(): int {
                let w = writeText("{{path}}", "x");
                var score = 0;
                if (exists("{{path}}")) { score = score + 10; }
                let r = remove("{{path}}");
                if (exists("{{path}}")) { score = score + 1; }
                return score;
            }
            """).Exit);
    }

    [Fact]
    public void Lines_are_counted_without_a_trailing_empty_one()
    {
        // Eine Datei, die mit einem Zeilenumbruch endet, hat danach keine leere letzte Zeile.
        // Ohne diese Regel zählte jede normal geschriebene Textdatei eine Zeile zu viel.
        var path = Path.Combine(_dir, "lines.txt").Replace("\\", "\\\\");
        Assert.Equal(3, Run($$"""
            import std.io.file { writeText, readLines };
            fn main(): int {
                let w = writeText("{{path}}", "a\nb\nc\n");
                return readLines("{{path}}").length;
            }
            """).Exit);
    }

    [Fact]
    public void Lines_of_a_missing_file_are_empty()
    {
        var path = Path.Combine(_dir, "nichtda.txt").Replace("\\", "\\\\");
        Assert.Equal(0, Run($$"""
            import std.io.file { readLines };
            fn main(): int { return readLines("{{path}}").length; }
            """).Exit);
    }

    [Fact]
    public void Append_adds_to_an_existing_file()
    {
        var path = Path.Combine(_dir, "app.txt").Replace("\\", "\\\\");
        Assert.Equal(2, Run($$"""
            import std.io.file { writeText, appendText, readLines };
            fn main(): int {
                let w = writeText("{{path}}", "eins\n");
                let a = appendText("{{path}}", "zwei\n");
                return readLines("{{path}}").length;
            }
            """).Exit);
    }
}
