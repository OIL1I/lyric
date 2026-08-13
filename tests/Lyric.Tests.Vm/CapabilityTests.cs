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
/// Capabilities (ADR-007, Doku §20.1) — M8/S6.
///
/// <para>THE REQUIREMENT STANDS IN THE MODULE, THE DECISION AT THE RUNTIME. The compiler writes into the
/// capabilities section WHAT a program wants to touch; at load time the VM checks against WHAT it grants.
/// The separation is not cosmetic: a `.lyrbc` can come from elsewhere, and a host loading foreign
/// bytecode has never seen the compiler. A pure resolve-time check would be worthless to it.</para>
/// </summary>
public class CapabilityTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static BytecodeModule Compile(string source)
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
        Assert.False(de.HasErrors, "source did not compile: " + writer);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);
        return BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!));
    }

    private const string UsesOs = """
        import std.os { platform };
        fn main(): int { let p = platform(); return 0; }
        """;

    // ------------------------------------------------------------------ the requirement in the module

    [Fact]
    public void A_program_that_touches_nothing_requires_nothing() =>
        Assert.Equal(0UL, Compile("fn main(): int { return 0; }").Capabilities);

    [Fact]
    public void Importing_a_gated_module_records_the_requirement() =>
        // The bit mask is part of the bytecode contract: 'osAccess' is bit 2. A test on the numeric value
        // rather than only on "not equal to 0", because a shifted assignment would make every older .lyrbc
        // wrong.
        Assert.Equal((ulong)Capability.OsAccess, Compile(UsesOs).Capabilities);

    [Fact]
    public void The_requirement_survives_a_round_trip() =>
        // It really stands IN the module rather than being kept alongside; the test goes through the writer
        // and the reader.
        Assert.Equal((ulong)Capability.OsAccess,
            BytecodeReader.ReadOrThrow(BytecodeWriter.Write(
                new Lyric.Ir.IrModule([]) { Capabilities = Capability.OsAccess })).Capabilities);

    // ------------------------------------------------------------------ the enforcement

    [Fact]
    public void A_runtime_that_grants_everything_runs_it() =>
        Assert.Equal(0, Interpreter.Run(Compile(UsesOs), [],
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null),
            Capability.All).AsI64);

    [Fact]
    public void A_runtime_that_grants_the_right_capability_runs_it() =>
        Assert.Equal(0, Interpreter.Run(Compile(UsesOs), [],
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null),
            Capability.OsAccess).AsI64);

    [Fact]
    public void A_runtime_that_grants_nothing_refuses()
    {
        var refused = Assert.Throws<LyricRuntimeException>(() => Interpreter.Run(Compile(UsesOs), [],
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null),
            Capability.None));

        Assert.Equal("LYR-CAP0001", refused.Code);
        Assert.Contains("osAccess", refused.Message);
    }

    [Fact]
    public void The_wrong_capability_does_not_help() =>
        // The counter-check to the test above: 'some' capability does not suffice. Without it the check
        // would stay green even if it only looked at "granted != None".
        Assert.Throws<LyricRuntimeException>(() => Interpreter.Run(Compile(UsesOs), [],
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null),
            Capability.FileAccess));

    [Fact]
    public void A_program_without_requirements_runs_in_a_sandbox() =>
        // The other direction: the check must not block everything running in a narrow VM. A program
        // requiring nothing runs with 'none' too.
        Assert.Equal(7, Interpreter.Run(Compile("fn main(): int { return 7; }"), [],
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null),
            Capability.None).AsI64);

    // ------------------------------------------------------------------ die Tabelle

    [Fact]
    public void Submodules_inherit_the_requirement_of_their_parent() =>
        // Otherwise every new submodule would be a silent gap: 'std.os.env' has to cost the same as
        // 'std.os'.
        Assert.Equal(Capability.OsAccess, CapabilityTable.RequiredForImport("std.os.env"));

    [Fact]
    public void An_always_allowed_module_costs_nothing()
    {
        Assert.Equal(Capability.None, CapabilityTable.RequiredForImport("std.string"));
        Assert.Equal(Capability.None, CapabilityTable.RequiredForImport("std.collections"));
        Assert.Equal(Capability.None, CapabilityTable.RequiredForImport("std.io.console"));
    }

    [Fact]
    public void A_similar_name_is_not_gated() =>
        // 'std.ostrich' starts with 'std.os' but is a different module. Without the dot boundary in the
        // comparison that would be a wrong rejection.
        Assert.Equal(Capability.None, CapabilityTable.RequiredForImport("std.ostrich"));

    [Fact]
    public void An_unknown_capability_name_is_rejected() =>
        // 'null' rather than 'None': silently granting less than requested would be the dangerous answer,
        // and the caller should report.
        Assert.Null(CapabilityTable.Parse("file,quantum"));
}
