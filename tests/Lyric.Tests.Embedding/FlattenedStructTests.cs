using System.Runtime.CompilerServices;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// A struct parameter in a native signature crosses the boundary FLATTENED: the .lyr declaration
/// says <c>Vec2</c>, the wire signature says two floats, and the host registers exactly the
/// delegate it would have written for two scalar parameters. No object crosses, so the host
/// learns no module layout — the boundary rule, kept, with better ergonomics on the script side.
/// </summary>
public class FlattenedStructTests : IDisposable
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-flat-" + Guid.NewGuid().ToString("N")[..8]);

    public FlattenedStructTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Sdk(string modulePath, string source)
    {
        var file = Path.Combine(_dir, Path.Combine(modulePath.Split('.')) + ".lyr");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, source);
        return _dir;
    }

    private LangVm Vm() => new(new HostOptions
    {
        StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
        NativeRoots = new Dictionary<string, string>(StringComparer.Ordinal) { ["engine"] = _dir },
    });

    [Fact]
    public void A_struct_parameter_arrives_as_its_fields()
    {
        Sdk("engine.geo", """
            module engine.geo;

            pub struct Vec2 { x: float, y: float }

            pub fn len2(v: Vec2): float;
            """);

        var vm = Vm();
        // The host's delegate has the WIRE signature — two doubles — not the struct. That is
        // the whole contract: the SDK declaration is the typed façade, the host stays scalar.
        vm.RegisterNative("engine.geo.len2", (double x, double y) => x * x + y * y);

        var instance = vm.Instantiate(vm.Compile("""
            import engine.geo { Vec2, len2 };

            pub fn probe(): int {
                let v = Vec2 { x = 3.0, y = 4.0 };
                return len2(v) as int;
            }
            """, "game"));

        Assert.Equal(25L, instance.Call<long>("probe"));
    }

    [Fact]
    public void Scalars_and_a_struct_mix_in_declaration_order()
    {
        Sdk("engine.geo", """
            module engine.geo;

            pub struct Vec2 { x: float, y: float }

            pub fn place(entity: int, at: Vec2, size: float): int;
            """);

        var vm = Vm();
        vm.RegisterNative("engine.geo.place",
            (long entity, double x, double y, double size) =>
                entity + (long)x * 10 + (long)y * 100 + (long)size * 1000);

        var instance = vm.Instantiate(vm.Compile("""
            import engine.geo { Vec2, place };

            pub fn probe(): int {
                return place(7, Vec2 { x = 3.0, y = 4.0 }, 2.0);
            }
            """, "game"));

        Assert.Equal(7 + 30 + 400 + 2000L, instance.Call<long>("probe"));
    }

    [Fact]
    public void The_fields_are_a_snapshot_taken_at_the_call()
    {
        Sdk("engine.geo", """
            module engine.geo;

            pub struct Vec2 { x: float, y: float }

            pub fn firstX(v: Vec2): float;
            """);

        var vm = Vm();
        vm.RegisterNative("engine.geo.firstX", (double x, double y) => x);

        // Value semantics across the boundary: the native saw the fields as they were AT the
        // call, later mutation included nothing.
        var instance = vm.Instantiate(vm.Compile("""
            import engine.geo { Vec2, firstX };

            pub fn probe(): int {
                var v = Vec2 { x = 1.0, y = 2.0 };
                let seen = firstX(v);
                v.x = 99.0;
                return (seen + v.x) as int;
            }
            """, "game"));

        Assert.Equal(100L, instance.Call<long>("probe"));
    }

    [Fact]
    public void A_struct_with_a_reference_field_is_rejected_at_compile_time()
    {
        Sdk("engine.geo", """
            module engine.geo;

            pub struct Bad { xs: int[] }

            pub fn take(b: Bad): int;
            """);

        var vm = Vm();

        var cause = Assert.Throws<EmbeddingException>(() => vm.Compile("""
            import engine.geo { Bad, take };

            pub fn probe(): int { return take(Bad { xs = [1] }); }
            """, "game"));

        Assert.Contains(cause.Diagnostics,
            d => d.Message.Contains("scalar and string fields only", StringComparison.Ordinal));
    }
}
