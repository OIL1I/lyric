using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// Where a compilation error happened, as the host learns it.
///
/// <para>A compiler diagnostic carries a <c>Span</c>: a file INDEX plus offsets, resolvable only
/// through the source manager that handed the index out — which belongs to one compilation and is
/// gone by the time a host catches the exception. What arrived was a code and a message, and in a
/// project of thirteen files that is the question "in which one?" every time.</para>
///
/// <para>The interesting case is the last one: an error in an IMPORTED module. Naming the entry
/// file there would be worse than naming nothing, because it would be confidently wrong.</para>
/// </summary>
public sealed class DiagnosticPositionTests : IDisposable
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-diag-" + Guid.NewGuid().ToString("N")[..8]);

    public DiagnosticPositionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Write(string relativePath, string source)
    {
        var file = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, source);
        return file;
    }

    private LangVm Vm() => new(new HostOptions
    {
        StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
        SourceRoot = _dir,
        Capabilities = Capability.None,
    });

    [Fact]
    public void A_diagnostic_carries_its_line_and_column()
    {
        var thrown = Assert.Throws<EmbeddingException>(() => Vm().Compile("""
            fn main(): int {
                return gibtsNicht;
            }
            """, "mod"));

        var diagnostic = Assert.Single(thrown.Diagnostics, d => d.Code == "LYR-SEM0002");
        Assert.Equal(2, diagnostic.Line);
        Assert.Equal(12, diagnostic.Column);
        Assert.Contains("gibtsNicht", diagnostic.Message);
    }

    [Fact]
    public void A_module_compiled_from_memory_is_named_by_its_module_name()
    {
        // There is no path to give, so the name the host passed to Compile is what stands there —
        // never an empty string, which a message would render as ':3:5:'.
        var thrown = Assert.Throws<EmbeddingException>(
            () => Vm().Compile("fn main(): int { return gibtsNicht; }", "inventory"));

        Assert.All(thrown.Diagnostics, d => Assert.Equal("inventory", d.File));
    }

    [Fact]
    public void A_module_compiled_from_disk_carries_its_path()
    {
        var path = Write("main.lyr", """
            fn main(): int {
                return gibtsNicht;
            }
            """);

        var thrown = Assert.Throws<EmbeddingException>(() => Vm().CompileFile(path));

        var diagnostic = Assert.Single(thrown.Diagnostics, d => d.Code == "LYR-SEM0002");
        Assert.Equal(path, diagnostic.File);
        Assert.Equal(2, diagnostic.Line);
    }

    [Fact]
    public void An_error_in_an_imported_module_names_that_module()
    {
        // The case the requirement was filed for. The entry file compiles cleanly; the fault is one
        // import away, and naming the entry would be confidently wrong.
        var helper = Write("helper.lyr", """
            module helper;

            pub fn double(n: int): int {
                return n * missing;
            }
            """);
        var entry = Write("main.lyr", """
            import helper { double };

            fn main(): int {
                return double(21);
            }
            """);

        var thrown = Assert.Throws<EmbeddingException>(() => Vm().CompileFile(entry));

        var diagnostic = Assert.Single(thrown.Diagnostics, d => d.Code == "LYR-SEM0002");
        Assert.Equal(helper, diagnostic.File);
        Assert.Equal(4, diagnostic.Line);
    }

    [Fact]
    public void A_note_carries_its_own_place()
    {
        // A note points at ANOTHER place of the same finding — the first declaration, here. Left
        // unresolved it would be the same riddle one level down.
        var thrown = Assert.Throws<EmbeddingException>(() => Vm().Compile("""
            fn twice(): int {
                return 1;
            }

            fn twice(): int {
                return 2;
            }

            fn main(): int {
                return twice();
            }
            """, "mod"));

        var diagnostic = thrown.Diagnostics.First(d => d.Severity == Severity.Error);
        var note = Assert.Single(diagnostic.Notes);
        Assert.Equal("mod", note.File);
        Assert.Equal(1, note.Line);
    }

    [Fact]
    public void The_text_form_is_the_one_the_command_line_prints()
    {
        var thrown = Assert.Throws<EmbeddingException>(
            () => Vm().Compile("fn main(): int { return gibtsNicht; }", "mod"));

        var diagnostic = Assert.Single(thrown.Diagnostics, d => d.Code == "LYR-SEM0002");
        Assert.StartsWith("mod:1:25: error[LYR-SEM0002]: ", diagnostic.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_panic_carries_the_backtrace_the_runtime_built()
    {
        // The same question one level over: the frames exist, and reaching them used to mean
        // naming LyricPanic — a type of the runtime assembly this API exists so a host need not
        // reference.
        var vm = Vm();
        var instance = vm.Instantiate(vm.Compile("""
            import std.core { panic };

            fn inner(): int {
                panic("no");
            }

            pub fn outer(): int {
                return inner();
            }
            """, "mod"));

        var thrown = Assert.Throws<ScriptPanicException>(() => instance.Call<long>("outer"));

        Assert.NotEmpty(thrown.Backtrace);
        Assert.Contains(thrown.Backtrace, frame => frame.Contains("mod"));
    }
}
