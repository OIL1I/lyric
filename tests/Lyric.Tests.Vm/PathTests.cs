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
/// The path helpers from `std.io.file`, ALL WRITTEN IN LYRIC.
///
/// <para>A path is a string, and searching for separators is something the language can do itself. The
/// host would only bring its own platform convention here, and with a platform-neutral bytecode that is
/// exactly the wrong thing: the same `.lyrbc` has to produce the same path on every system.</para>
///
/// <para>BOTH separators therefore apply. Windows understands `/`, and a script running on both systems
/// should not have to guess which one is current.</para>
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
    [InlineData("joinPath(\"a/\", \"c\")", "[a/c]")]          // do not double the separator
    [InlineData("joinPath(\"a\", \"/c\")", "[a/c]")]
    [InlineData("joinPath(\"a/\", \"/c\")", "[a/c]")]         // both have one
    [InlineData("joinPath(\"\", \"x\")", "[x]")]
    [InlineData("joinPath(\"x\", \"\")", "[x]")]
    public void JoinPath_never_doubles_or_drops_the_separator(string expression, string expected) =>
        Assert.Equal(expected, Value(expression));

    [Theory]
    [InlineData("fileName(\"a/b/c.txt\")", "[c.txt]")]
    [InlineData("fileName(\"c.txt\")", "[c.txt]")]            // without a folder
    [InlineData("fileName(\"a/b/\")", "[]")]                  // ends in a separator
    [InlineData("parentDir(\"a/b/c.txt\")", "[a/b]")]
    [InlineData("parentDir(\"c.txt\")", "[]")]                // no folder
    [InlineData("parentDir(\"/x\")", "[/]")]                  // the root stays the root
    public void The_name_and_the_parent_split_at_the_last_separator(
        string expression, string expected) =>
        Assert.Equal(expected, Value(expression));

    /// <summary>
    /// A leading dot is NO extension: `.gitignore` is called that, it is not a "gitignore file without a
    /// name".
    /// </summary>
    /// <remarks>This rule differs between languages, and holding it here is cheaper than letting someone
    /// find it out.</remarks>
    [Theory]
    [InlineData("extension(\"a/b/c.txt\")", "[txt]")]
    [InlineData("extension(\".gitignore\")", "[]")]
    [InlineData("extension(\"ohne\")", "[]")]
    [InlineData("extension(\"a.tar.gz\")", "[gz]")]           // the last one only
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
        // The point of the whole exercise: the same .lyrbc has to produce the same path on every system.
        // A host native would bring its own convention here.
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

    // ------------------------------------------------- the two fixes from this slice

    /// <summary>
    /// An optional native over a SCALAR yields its value.
    ///
    /// <para><c>size</c> is the first optional native with a scalar return type; all the earlier ones
    /// yield <c>string</c> and carry their reference themselves. For a <c>?int</c> only a marker in
    /// <c>Ref</c> marks presence — every bit pattern is a valid number, so there is none for "no
    /// value".</para>
    /// <para>Without <c>LyrValue.Some</c> it silently returned <c>null</c>: the file existed,
    /// <c>isFile</c> saw it, and <c>size</c> reported nothing.</para>
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
    /// <c>?T[] ?? []</c> — an empty array literal takes its element type from the context.
    /// </summary>
    /// <remarks><c>CheckCoalesce</c> asks only <see cref="TypeChecker"/>'s <c>IsAssignable</c> rather than
    /// <c>CheckAssignable</c>, so the adaptation that already existed for arguments was never reached
    /// there. Both places now use the same function.</remarks>
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
