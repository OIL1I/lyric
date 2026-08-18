using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// The argument buffers behind native calls are pooled per program. The contract has two halves:
/// the buffer is valid for the whole duration of the call — including while the implementation
/// calls BACK into the VM — and it is reused afterwards.
///
/// <para>The reentrancy case is the sharp one: a shared single buffer would pass every ordinary
/// test and corrupt exactly the native that re-enters. The test builds that native by overriding
/// a stdlib implementation, because only stdlib modules may declare natives — the override
/// re-enters the program mid-call and then reads its own arguments again.</para>
/// </summary>
public class ArgumentPoolTests
{
    private static (BytecodeModule Module, DiagnosticEngine De) Compile(string source)
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

        var writer = new StringWriter();
        de.RenderText(writer);
        Assert.False(de.HasErrors, "source did not compile:\n" + writer);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        return (BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)), de);
    }

    private static string RepoRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    [Fact]
    public void A_native_that_reenters_the_vm_keeps_its_own_arguments()
    {
        // '@Hook' is load-bearing: 'inner' has no caller the compiler can see, and without the
        // attribute row the reachability pruning would remove exactly the function the host
        // wants to call — the documented reason attributed functions are roots.
        var (module, _) = Compile("""
            import std.core { OnFunction };
            import std.math;

            pub struct Hook :: [OnFunction] { }

            @Hook
            pub fn inner(): float {
                // Rents an arity-1 buffer while the sqrt below still holds its own.
                return math.abs(0.0 - 7.0);
            }

            fn main(): int {
                let r = math.sqrt(81.0);
                return if (r == 81.0) 0 else 1;
            }
            """);

        // The override re-enters the program between two reads of its argument. With one shared
        // buffer per arity the inner abs would overwrite the 81 with -7; the pool hands the
        // inner call the NEXT buffer because this one is checked out.
        LoadedProgram? program = null;
        var natives = NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null);
        natives.Register("std.math.sqrt", [TypeTag.F64], TypeTag.F64, arguments =>
        {
            var before = arguments[0].AsF64;
            program!.Invoke(program.IndexOfFunction("main.inner"));
            var after = arguments[0].AsF64;

            return LyrValue.FromF64(before == after ? before : double.NaN);
        });

        program = LoadedProgram.Load(module, natives, Capability.None);

        Assert.Equal(0, program.RunEntry([]).AsI64);
    }

    [Fact]
    public void Repeated_native_calls_reuse_their_buffer()
    {
        var (module, _) = Compile("""
            import std.math;

            fn main(): int {
                var acc = 0.0;
                var i = 0;
                while (i < 1000) {
                    acc = acc + math.abs(0.0 - 1.0);
                    i = i + 1;
                }
                return if (acc == 1000.0) 0 else 1;
            }
            """);

        var program = LoadedProgram.Load(module,
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null), Capability.None);

        // Warm once, then a thousand native calls must not allocate a thousand buffers. The
        // bound is loose on purpose — it fails against per-call allocation (40 B each), not
        // against unrelated runtime noise.
        program.RunEntry([]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        program.RunEntry([]);
        var grown = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(grown < 10_000,
            $"1000 native calls allocated {grown} bytes — the argument pool is not reusing");
    }
}
