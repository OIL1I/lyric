using System.Runtime.CompilerServices;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Embedding;
using Lyric.Vm;

namespace Lyric.Tests.Embedding;

/// <summary>
/// A native that RETURNS a struct: on the wire the result comes back through a hidden buffer the
/// runtime passes as a trailing argument, and the call site copies the value out. The sharp
/// question of this file is the SHARED buffer: two calls, one buffer — value semantics must keep
/// the first result intact, because every binding copies.
///
/// <para>Compilation goes through the embedding layer (it knows native roots); execution goes
/// through the RAW registry, because <see cref="NativeRegistry.RegisterStructReturning"/> is the
/// VM-level surface a game host uses.</para>
/// </summary>
public class StructReturnTests : IDisposable
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-sret-" + Guid.NewGuid().ToString("N")[..8]);

    public StructReturnTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private LoadedProgram Load(string sdkSource, string gameSource, NativeRegistry natives)
    {
        var file = Path.Combine(_dir, "engine", "geo.lyr");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, sdkSource);

        var vm = new LangVm(new HostOptions
        {
            StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
            NativeRoots = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["engine"] = _dir,
            },
        });

        var module = VmHost.Load(vm.Compile(gameSource, "game").Bytes, Console.Error)
                     ?? throw new InvalidOperationException("module did not load");
        return LoadedProgram.Load(module, natives, Capability.None);
    }

    private const string Sdk = """
        module engine.geo;

        pub struct Vec2 { x: float, y: float }

        pub fn positionOf(entity: int): Vec2;
        """;

    private static NativeRegistry Registry()
    {
        // The entity id decides the fields, so two calls return two different values through
        // the ONE buffer — which is what the tests below lean on.
        var natives = NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null);
        natives.RegisterStructReturning("engine.geo.positionOf",
            [TypeTag.I64], [TypeTag.F64, TypeTag.F64],
            (arguments, result) =>
            {
                var entity = arguments[0].AsI64;
                result[0] = LyrValue.FromF64(entity * 10.0);
                result[1] = LyrValue.FromF64(entity * 10.0 + 1.0);
            });
        return natives;
    }

    [Fact]
    public void A_struct_comes_back_as_a_value()
    {
        var program = Load(Sdk, """
            import engine.geo { positionOf };

            fn main(): int {
                let p = positionOf(3);
                return (p.x + p.y) as int;
            }
            """, Registry());

        Assert.Equal(61L, program.RunEntry([]).AsI64);
    }

    [Fact]
    public void Two_results_through_one_buffer_stay_two_values()
    {
        // The sharp case: 'a' is bound BEFORE the second call refills the buffer. Value
        // semantics copies at the binding, so a keeps (10, 11) while b holds (20, 21) — with a
        // shared result without the copy, both would read (20, 21).
        var program = Load(Sdk, """
            import engine.geo { positionOf };

            fn main(): int {
                let a = positionOf(1);
                let b = positionOf(2);
                return (a.x * 1000.0 + b.x) as int;
            }
            """, Registry());

        Assert.Equal(10_020L, program.RunEntry([]).AsI64);
    }

    [Fact]
    public void An_escaping_result_survives_later_calls()
    {
        // Stored into a class field, the value leaves the frame — the copy is real there, and
        // later calls must not reach it.
        var program = Load(Sdk, """
            import engine.geo { Vec2, positionOf };

            class Halter { p: Vec2 = Vec2 { x = 0.0, y = 0.0 } }

            let halter = Halter { };

            fn main(): int {
                halter.p = positionOf(4);
                positionOf(9);
                return halter.p.x as int;
            }
            """, Registry());

        Assert.Equal(40L, program.RunEntry([]).AsI64);
    }

    [Fact]
    public void A_disagreeing_host_layout_is_rejected_at_load_time()
    {
        var natives = NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null);
        natives.RegisterStructReturning("engine.geo.positionOf",
            [TypeTag.I64], [TypeTag.F64, TypeTag.F64, TypeTag.F64],   // one field too many
            (arguments, result) => { });

        var cause = Assert.Throws<LyricRuntimeException>(() => Load(Sdk, """
            import engine.geo { positionOf };

            fn main(): int { return positionOf(1).x as int; }
            """, natives));

        Assert.Contains("layout", cause.Message, StringComparison.Ordinal);
    }
}
