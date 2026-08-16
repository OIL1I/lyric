using System.Runtime.CompilerServices;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// An SDK that ships its surface as <c>.lyr</c> files instead of generating it.
///
/// <para>The two halves are independent on purpose: a root decides that a module MAY declare a
/// function without a body, and <c>RegisterNative</c> supplies what it does. A test for each, and one
/// that shows what happens when only the first is there — a declaration nobody implements has to fail
/// loudly rather than silently do nothing.</para>
/// </summary>
public class NativeRootTests : IDisposable
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-sdk-" + Guid.NewGuid().ToString("N")[..8]);

    public NativeRootTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Writes an SDK module and returns the root it is rooted at.</summary>
    private string Sdk(string modulePath, string source)
    {
        var file = Path.Combine(_dir, Path.Combine(modulePath.Split('.')) + ".lyr");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, source);
        return _dir;
    }

    private LangVm Vm(string? nativeSegment = null) => new(new HostOptions
    {
        StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
        NativeRoots = nativeSegment is null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { [nativeSegment] = _dir },
    });

    [Fact]
    public void A_declaration_in_a_native_root_binds_to_the_host()
    {
        Sdk("engine.input", """
            module engine.input;

            pub fn keyDown(key: int): bool;
            """);

        var vm = Vm("engine");
        vm.RegisterNative("engine.input.keyDown", (long key) => key == 32);

        var instance = vm.Instantiate(vm.Compile("""
            import engine.input { keyDown };

            pub fn space(): bool { return keyDown(32); }
            pub fn other(): bool { return keyDown(27); }
            """, "script"));

        Assert.True(instance.Call<bool>("space"));
        Assert.False(instance.Call<bool>("other"));
    }

    [Fact]
    public void Without_the_root_the_same_file_is_a_compiler_error()
    {
        // The root is what decides, not the file. This is the whole point of keying it by origin:
        // the same bytes on disk are a declaration in one place and a missing body in another.
        Sdk("engine.input", """
            module engine.input;

            pub fn keyDown(key: int): bool;
            """);

        var vm = Vm(); // no native root at all
        vm.RegisterNative("engine.input.keyDown", (long _) => true);

        var thrown = Assert.Throws<EmbeddingException>(() => vm.Compile("""
            import engine.input { keyDown };

            pub fn space(): bool { return keyDown(32); }
            """, "script"));

        // Not "cannot find module": without the root the segment belongs to the program's own
        // directory, and there is no program directory for source held in memory.
        Assert.NotEmpty(thrown.Diagnostics);
    }

    [Fact]
    public void A_declaration_nobody_implements_fails_at_load()
    {
        // The honest failure of the half-configured case. It has to be loud: a host that ships a
        // declaration and forgets the implementation would otherwise find out at the call site.
        Sdk("engine.input", """
            module engine.input;

            pub fn keyDown(key: int): bool;
            """);

        var vm = Vm("engine"); // root, but no RegisterNative

        var module = vm.Compile("""
            import engine.input { keyDown };

            pub fn space(): bool { return keyDown(32); }
            """, "script");

        var thrown = Assert.Throws<ScriptException>(() => vm.Instantiate(module));
        Assert.Contains("engine.input.keyDown", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_native_root_module_may_hold_ordinary_code_too()
    {
        // 'may declare a function without a body' is a permission, not an obligation. An SDK ships
        // helpers written in Lyric beside its declarations.
        Sdk("engine.math", """
            module engine.math;

            pub fn raw(n: int): int;

            pub fn twice(n: int): int { return raw(n) * 2; }
            """);

        var vm = Vm("engine");
        vm.RegisterNative("engine.math.raw", (long n) => n + 1);

        var instance = vm.Instantiate(vm.Compile("""
            import engine.math { twice };

            pub fn go(): int { return twice(20); }
            """, "script"));

        Assert.Equal(42, instance.Call<long>("go"));
    }

    [Fact]
    public void An_unqualified_native_name_is_rejected()
    {
        var thrown = Assert.Throws<ArgumentException>(
            () => Vm("engine").RegisterNative("keyDown", () => true));

        Assert.Contains("qualified", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_host_module_cannot_be_written_into()
    {
        // Two mechanisms for one module would mean the generated source does not describe what the
        // module holds.
        var thrown = Assert.Throws<ArgumentException>(
            () => Vm("engine").RegisterNative("host.sneak", () => true));

        Assert.Contains(LangVm.HostModule, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_native_cannot_be_registered_twice()
    {
        var vm = Vm("engine");
        vm.RegisterNative("engine.input.keyDown", (long _) => true);

        Assert.Throws<ArgumentException>(
            () => vm.RegisterNative("engine.input.keyDown", (long _) => false));
    }
}
