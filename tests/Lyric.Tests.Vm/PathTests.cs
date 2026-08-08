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
/// Die Pfad-Helfer aus `std.io.file` (M8b/S8) — <b>alle in Lyric</b>.
///
/// <para>Ein Pfad ist ein String, und Trennzeichen zu suchen kann die Sprache selbst. Der Host
/// würde hier nur seine eigene Plattform-Konvention hineinbringen, und die ist bei einem
/// plattformneutralen Bytecode (ADR-013) genau das Falsche: dieselbe `.lyrbc` muss auf jedem
/// System denselben Pfad ergeben.</para>
///
/// <para>Deshalb gelten <b>beide</b> Trennzeichen. Windows versteht `/`, und ein Skript, das auf
/// beiden Systemen läuft, soll nicht raten müssen, welches gerade gilt.</para>
/// </summary>
public class PathTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Out(string body)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", body);
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
        import std.io.console { println };
        import std.io.file { joinPath, fileName, parentDir, extension, stem, withExtension,
                             isAbsolute };

        """;

    private static string Value(string expression) =>
        Out(Head + $"fn main(): int {{ println(\"[\" + {expression} + \"]\"); return 0; }}");

    [Theory]
    [InlineData("joinPath(\"a/b\", \"c.txt\")", "[a/b/c.txt]")]
    [InlineData("joinPath(\"a/\", \"c\")", "[a/c]")]          // Trenner nicht verdoppeln
    [InlineData("joinPath(\"a\", \"/c\")", "[a/c]")]
    [InlineData("joinPath(\"a/\", \"/c\")", "[a/c]")]         // beide haben einen
    [InlineData("joinPath(\"\", \"x\")", "[x]")]
    [InlineData("joinPath(\"x\", \"\")", "[x]")]
    public void JoinPath_never_doubles_or_drops_the_separator(string expression, string expected) =>
        Assert.Equal(expected, Value(expression));

    [Theory]
    [InlineData("fileName(\"a/b/c.txt\")", "[c.txt]")]
    [InlineData("fileName(\"c.txt\")", "[c.txt]")]            // ohne Ordner
    [InlineData("fileName(\"a/b/\")", "[]")]                  // endet auf Trenner
    [InlineData("parentDir(\"a/b/c.txt\")", "[a/b]")]
    [InlineData("parentDir(\"c.txt\")", "[]")]                // kein Ordner
    [InlineData("parentDir(\"/x\")", "[/]")]                  // Wurzel bleibt Wurzel
    public void The_name_and_the_parent_split_at_the_last_separator(
        string expression, string expected) =>
        Assert.Equal(expected, Value(expression));

    /// <summary>
    /// Ein führender Punkt ist <b>keine</b> Endung: `.gitignore` heisst so, es ist kein
    /// „gitignore-Datei ohne Namen".
    /// </summary>
    /// <remarks>Diese Regel unterscheidet sich zwischen Sprachen, und sie hier festzuhalten ist
    /// billiger, als sie jemanden herausfinden zu lassen.</remarks>
    [Theory]
    [InlineData("extension(\"a/b/c.txt\")", "[txt]")]
    [InlineData("extension(\".gitignore\")", "[]")]
    [InlineData("extension(\"ohne\")", "[]")]
    [InlineData("extension(\"a.tar.gz\")", "[gz]")]           // nur die letzte
    [InlineData("stem(\"a/b/c.txt\")", "[c]")]
    [InlineData("stem(\".gitignore\")", "[.gitignore]")]
    [InlineData("stem(\"ohne\")", "[ohne]")]
    public void The_extension_stops_at_a_leading_dot(string expression, string expected) =>
        Assert.Equal(expected, Value(expression));

    [Theory]
    [InlineData("withExtension(\"a/b/c.txt\", \"md\")", "[a/b/c.md]")]
    [InlineData("withExtension(\"c.txt\", \"md\")", "[c.md]")]
    [InlineData("withExtension(\"c.txt\", \"\")", "[c]")]     // leere Endung entfernt sie
    [InlineData("withExtension(\"ohne\", \"txt\")", "[ohne.txt]")]
    public void WithExtension_swaps_the_suffix(string expression, string expected) =>
        Assert.Equal(expected, Value(expression));

    [Fact]
    public void Both_separators_are_recognised() =>
        // Der Punkt der ganzen Übung: dieselbe .lyrbc muss auf jedem System denselben Pfad
        // ergeben. Ein Host-Native brächte hier seine eigene Konvention mit.
        Assert.Equal("[c.txt]", Out("""
            import std.io.console { println };
            import std.io.file { fileName };
            import std.string { fromChar };

            fn main(): int {
                let windows = "a" + fromChar('\\') + "b" + fromChar('\\') + "c.txt";
                println("[" + fileName(windows) + "]");
                return 0;
            }
            """));

    [Fact]
    public void An_absolute_path_is_recognised_in_both_shapes() =>
        Assert.Equal("true true false false", Out("""
            import std.io.console { println };
            import std.io.file { isAbsolute };
            import std.string { fromChar };

            fn main(): int {
                let windows = "C:" + fromChar('\\') + "x";
                println(f"{isAbsolute("/x")} {isAbsolute(windows)} {isAbsolute("rel")} {isAbsolute("")}");
                return 0;
            }
            """));

    // ------------------------------------------------- die beiden Fixes aus diesem Slice

    /// <summary>
    /// Ein optionales Native über einem <b>Skalar</b> liefert seinen Wert.
    /// </summary>
    /// <remarks>
    /// <para><c>size</c> ist das erste optionale Native mit einem Skalar-Rückgabetyp; alle
    /// bisherigen liefern <c>string</c> und tragen ihre Referenz selbst. Bei einem <c>?int</c>
    /// markiert erst ein Marker in <c>Ref</c> die Anwesenheit — jedes Bitmuster ist eine gültige
    /// Zahl, es gibt also keins für „kein Wert" (Bytecode.md §5).</para>
    /// <para>Ohne <c>LyrValue.Some</c> gab es still <c>null</c> zurück: die Datei existierte,
    /// <c>isFile</c> sah sie, und <c>size</c> meldete nichts.</para>
    /// </remarks>
    [Fact]
    public void An_optional_native_over_a_scalar_returns_its_value() =>
        Assert.Equal("5 true", Out("""
            import std.io.console { println };
            import std.io.file { size, writeText, remove, joinPath, tempDir };

            fn main(): int {
                let pfad = joinPath(tempDir(), "lyric-size-probe.txt");
                writeText(pfad, "hallo");
                let groesse = size(pfad) ?? -1;
                let fehlt = size(joinPath(tempDir(), "gibtsnicht-xyz.txt")) == null;
                remove(pfad);
                println(f"{groesse} {fehlt}");
                return 0;
            }
            """));

    /// <summary>
    /// <c>?T[] ?? []</c> — ein leeres Array-Literal bekommt seinen Elementtyp aus dem
    /// <c>??</c>-Kontext.
    /// </summary>
    /// <remarks><c>CheckCoalesce</c> fragt nur <see cref="TypeChecker"/>s <c>IsAssignable</c> und
    /// nicht <c>CheckAssignable</c> — die Anpassung, die für Argumente schon da war, wurde dort
    /// nie erreicht. Beide Stellen benutzen jetzt dieselbe Funktion.</remarks>
    [Fact]
    public void An_empty_array_literal_takes_its_type_from_the_coalesce() =>
        Assert.Equal("0 2", Out("""
            import std.io.console { println };

            fn leer(): ?int[] { return null; }
            fn voll(): ?int[] { return [1, 2]; }

            fn main(): int {
                let a = leer() ?? [];
                let b = voll() ?? [];
                println(f"{a.length} {b.length}");
                return 0;
            }
            """));
}
