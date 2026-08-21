using System.Runtime.CompilerServices;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Embedding;
using Lyric.Vm;

namespace Lyric.Tests.Embedding;

/// <summary>
/// A native signature may name a value struct from ANOTHER module of the SDK.
///
/// <para>An SDK of several files declares its vector once. Before 2.5 only the declaring module
/// could name it, so a second module either duplicated the type — two layouts the host would have
/// to keep in step — or fell back to scalar pairs, which is the very form the value struct
/// replaced. The restriction was where the implementation stopped, not a property of the wire:
/// what crosses is a LAYOUT, and a layout belongs to the program rather than to the file that
/// wrote it down.</para>
///
/// <para>Compiling is only half the question here. The tests run the calls, because a signature
/// the compiler accepts and the binder flattens differently would be a wrong number rather than a
/// diagnostic.</para>
/// </summary>
public sealed class ForeignStructSignatureTests : IDisposable
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-foreign-" + Guid.NewGuid().ToString("N")[..8]);

    public ForeignStructSignatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void Sdk(string modulePath, string source)
    {
        var file = Path.Combine(_dir, Path.Combine(modulePath.Split('.')) + ".lyr");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, source);
    }

    private LangVm Vm() => new(new HostOptions
    {
        StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
        NativeRoots = new Dictionary<string, string>(StringComparer.Ordinal) { ["engine"] = _dir },
    });

    /// <summary>The module that owns the type — one declaration for the whole SDK.</summary>
    private void World() => Sdk("engine.world", """
        module engine.world;

        pub struct Vec2 { x: float, y: float }

        pub fn positionOf(entity: int): Vec2;
        """);

    private static NativeRegistry Registry()
    {
        var natives = NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null);

        // Both modules bind against the SAME wire shape: two doubles out, two doubles in. The
        // host never learns which module wrote the declaration down.
        natives.RegisterStructReturning("engine.world.positionOf",
            [TypeTag.I64], [TypeTag.F64, TypeTag.F64],
            (arguments, result) =>
            {
                result[0] = LyrValue.FromF64(arguments[0].AsI64 * 10.0);
                result[1] = LyrValue.FromF64(arguments[0].AsI64 * 10.0 + 1.0);
            });

        natives.RegisterStructReturning("engine.camera.toWorld",
            [TypeTag.F64, TypeTag.F64], [TypeTag.F64, TypeTag.F64],
            (arguments, result) =>
            {
                result[0] = LyrValue.FromF64(arguments[0].AsF64 + 100.0);
                result[1] = LyrValue.FromF64(arguments[1].AsF64 + 200.0);
            });

        natives.Register("engine.camera.zoomTo", [TypeTag.F64, TypeTag.F64], TypeTag.F64,
            arguments => LyrValue.FromF64(arguments[0].AsF64 + arguments[1].AsF64));

        return natives;
    }

    private LoadedProgram Load(string gameSource)
    {
        var vm = Vm();
        var module = VmHost.Load(vm.Compile(gameSource, "game").Bytes, Console.Error)
                     ?? throw new InvalidOperationException("module did not load");
        return LoadedProgram.Load(module, Registry(), Capability.None);
    }

    [Fact]
    public void A_qualified_foreign_struct_returns_across_the_boundary()
    {
        World();
        Sdk("engine.camera", """
            module engine.camera;

            import engine.world;

            pub fn toWorld(sx: float, sy: float): world.Vec2;
            """);

        var program = Load("""
            import engine.camera { toWorld };

            fn main(): int {
                let p = toWorld(1.0, 2.0);
                return (p.x + p.y) as int;
            }
            """);

        Assert.Equal(303L, program.RunEntry([]).AsI64);
    }

    [Fact]
    public void A_selectively_imported_foreign_struct_returns_across_the_boundary()
    {
        World();
        Sdk("engine.camera", """
            module engine.camera;

            import engine.world { Vec2 };

            pub fn toWorld(sx: float, sy: float): Vec2;
            """);

        var program = Load("""
            import engine.camera { toWorld };

            fn main(): int {
                let p = toWorld(3.0, 4.0);
                return (p.x + p.y) as int;
            }
            """);

        Assert.Equal(307L, program.RunEntry([]).AsI64);
    }

    [Fact]
    public void A_foreign_struct_flattens_as_a_parameter_too()
    {
        World();
        Sdk("engine.camera", """
            module engine.camera;

            import engine.world { Vec2 };

            pub fn zoomTo(at: Vec2): float;
            """);

        var program = Load("""
            import engine.world { Vec2 };
            import engine.camera { zoomTo };

            fn main(): int {
                return zoomTo(Vec2 { x = 20.0, y = 5.0 }) as int;
            }
            """);

        Assert.Equal(25L, program.RunEntry([]).AsI64);
    }

    [Fact]
    public void The_owning_module_still_names_its_own_struct()
    {
        // The counter-check: the path that worked since 1.7 goes through the same code now.
        World();

        var program = Load("""
            import engine.world { positionOf };

            fn main(): int {
                let p = positionOf(2);
                return (p.x + p.y) as int;
            }
            """);

        Assert.Equal(41L, program.RunEntry([]).AsI64);
    }

    [Fact]
    public void Both_modules_reach_one_type_in_one_program()
    {
        // What the requirement was filed for: the value a foreign-declared native returns is the
        // same type the owning module's native returns, so a script mixes them without a cast.
        World();
        Sdk("engine.camera", """
            module engine.camera;

            import engine.world { Vec2 };

            pub fn toWorld(sx: float, sy: float): Vec2;
            """);

        var program = Load("""
            import engine.world { Vec2, positionOf };
            import engine.camera { toWorld };

            fn sum(v: Vec2): float {
                return v.x + v.y;
            }

            fn main(): int {
                return (sum(positionOf(1)) + sum(toWorld(0.0, 0.0))) as int;
            }
            """);

        Assert.Equal(321L, program.RunEntry([]).AsI64);
    }

    [Fact]
    public void A_foreign_struct_with_an_unflattenable_field_is_still_refused()
    {
        // The boundary that stays: what crosses is scalars and strings. An array field would put
        // a module layout into the host's hands, and crossing a module line changes nothing
        // about that.
        Sdk("engine.world", """
            module engine.world;

            pub struct Path { points: float[] }
            """);
        Sdk("engine.camera", """
            module engine.camera;

            import engine.world { Path };

            pub fn walk(p: Path): float;
            """);

        var vm = Vm();
        var failure = Assert.Throws<EmbeddingException>(() => vm.Compile("""
            import engine.camera { walk };

            fn main(): int {
                return 0;
            }
            """, "game"));

        var diagnostic = Assert.Single(failure.Diagnostics, d => d.Code == "LYR-IR0001");
        Assert.Contains("flattened to scalars", diagnostic.Message);
    }
}
