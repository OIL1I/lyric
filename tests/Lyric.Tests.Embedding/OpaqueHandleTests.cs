using System.Runtime.CompilerServices;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// The opaque handle (v1.15) — Erato's A4, end to end: an SDK declares
/// <c>pub opaque type Entity = int;</c> beside its natives, a script can hold, store and pass
/// the handle, and it can neither forge one from a literal nor leak it into arithmetic. On the
/// wire the handle IS its underlying int, so the host's delegate stays scalar.
/// </summary>
public sealed class OpaqueHandleTests : IDisposable
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lyric-opaque-" + Guid.NewGuid());

    public OpaqueHandleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void Sdk()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "engine"));
        File.WriteAllText(Path.Combine(_dir, "engine", "world.lyr"), """
            module engine.world;

            /// A handle to an entity. Opaque: a script holds it, stores it and hands it back,
            /// and can neither forge one nor compute with it.
            pub opaque type Entity = int;

            pub fn spawn(): Entity;
            pub fn destroy(e: Entity): bool;
            """);
    }

    private LangVm Vm()
    {
        Sdk();
        return new LangVm(new HostOptions
        {
            StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
            NativeRoots = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["engine"] = _dir,
            },
        });
    }

    [Fact]
    public void A_handle_crosses_the_boundary_as_its_underlying()
    {
        var vm = Vm();
        var next = 40L;
        var destroyed = new List<long>();
        vm.RegisterNative("engine.world.spawn", () => ++next);
        vm.RegisterNative("engine.world.destroy", (long e) => { destroyed.Add(e); return true; });

        var instance = vm.Instantiate(vm.Compile("""
            import engine.world { Entity, spawn, destroy };

            pub fn probe(): bool {
                let a = spawn();
                let b = spawn();
                let survivor = if (a == b) a else b;
                return destroy(survivor);
            }
            """, "game"));

        Assert.True(instance.Call<bool>("probe"));
        Assert.Equal([42L], destroyed);
    }

    /// <summary>Erato's A9: an SDK is several modules, and its handle types want to cross
    /// module boundaries — one module owns the type, a sibling names it in its natives.</summary>
    private void MultiModuleSdk()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "engine"));
        File.WriteAllText(Path.Combine(_dir, "engine", "world.lyr"), """
            module engine.world;

            /// A handle to a texture, owned by the world module.
            pub opaque type TextureId = int;
            """);
        File.WriteAllText(Path.Combine(_dir, "engine", "assets.lyr"), """
            module engine.assets;

            import engine.world { TextureId };

            pub fn load(name: string): TextureId;
            pub fn unload(t: TextureId): bool;
            pub fn loaded(): TextureId[];
            """);
        File.WriteAllText(Path.Combine(_dir, "engine", "atlas.lyr"), """
            module engine.atlas;

            import engine.world;

            pub fn first(): world.TextureId;
            """);
    }

    [Fact]
    public void An_imported_opaque_type_resolves_in_a_native_signature()
    {
        MultiModuleSdk();
        var vm = new LangVm(new HostOptions
        {
            StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
            NativeRoots = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["engine"] = _dir,
            },
        });

        var unloaded = new List<long>();
        vm.RegisterNative("engine.assets.load", (string name) => (long)name.Length);
        vm.RegisterNative("engine.assets.unload", (long t) => { unloaded.Add(t); return true; });
        vm.RegisterNative("engine.atlas.first", () => 7L);

        // Every native declaration of the SDK is lowered when a script compiles against it, used
        // or not — so 'loaded(): TextureId[]' proves the ARRAY form resolves without needing a
        // binding here; only what the script calls has to be registered.
        var instance = vm.Instantiate(vm.Compile("""
            import engine.world { TextureId };
            import engine.assets { load, unload };
            import engine.atlas { first };

            pub fn probe(): bool {
                let grass: TextureId = load("grass");
                unload(grass);
                return unload(first());
            }
            """, "game"));

        Assert.True(instance.Call<bool>("probe"));
        Assert.Equal([5L, 7L], unloaded);
    }

    [Fact]
    public void A_forged_handle_is_refused_at_compile_time()
    {
        var vm = Vm();
        vm.RegisterNative("engine.world.spawn", () => 1L);
        vm.RegisterNative("engine.world.destroy", (long e) => true);

        // 'destroy(42)' is the exact bug A4 names: a guessed handle. It fails to COMPILE now.
        var ex = Record.Exception(() => vm.Compile("""
            import engine.world { destroy };

            pub fn probe(): bool {
                return destroy(42);
            }
            """, "game"));
        Assert.NotNull(ex);
    }
}
