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
/// <para><b>Der Bedarf steht im Modul, die Entscheidung bei der Runtime.</b> Der Compiler
/// schreibt in die Capabilities-Sektion, <b>was</b> ein Programm anfassen will; beim Laden prüft
/// die VM gegen das, <b>was</b> sie gewährt. Die Trennung ist nicht kosmetisch: ein `.lyrbc` kann
/// von woanders kommen, und ein Host, der fremden Bytecode lädt, hat den Compiler nie gesehen.
/// Eine reine Resolve-Zeit-Prüfung — wie ADR-007 sie nennt — wäre für ihn wertlos.</para>
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

    // ------------------------------------------------------------------ der Bedarf im Modul

    [Fact]
    public void A_program_that_touches_nothing_requires_nothing() =>
        Assert.Equal(0UL, Compile("fn main(): int { return 0; }").Capabilities);

    [Fact]
    public void Importing_a_gated_module_records_the_requirement() =>
        // Die Bitmaske ist Bytecode-Vertrag: 'osAccess' ist Bit 2. Ein Test auf den Zahlenwert
        // und nicht nur auf "ungleich 0", weil eine verschobene Zuordnung jedes ältere .lyrbc
        // falsch machen würde.
        Assert.Equal((ulong)Capability.OsAccess, Compile(UsesOs).Capabilities);

    [Fact]
    public void The_requirement_survives_a_round_trip() =>
        // Sie steht wirklich IM Modul und wird nicht nebenher geführt — der Test geht durch
        // Writer und Reader.
        Assert.Equal((ulong)Capability.OsAccess,
            BytecodeReader.ReadOrThrow(BytecodeWriter.Write(
                new Lyric.Ir.IrModule([]) { Capabilities = Capability.OsAccess })).Capabilities);

    // ------------------------------------------------------------------ die Durchsetzung

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
        // Die Gegenprobe zum Test darüber: 'irgendeine' Capability genügt nicht. Ohne ihn bliebe
        // die Prüfung auch grün, wenn sie nur auf "granted != None" sähe.
        Assert.Throws<LyricRuntimeException>(() => Interpreter.Run(Compile(UsesOs), [],
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null),
            Capability.FileAccess));

    [Fact]
    public void A_program_without_requirements_runs_in_a_sandbox() =>
        // Die andere Richtung: die Prüfung darf nicht alles blockieren, was in einer engen VM
        // läuft. Ein Programm, das nichts verlangt, läuft auch mit 'none'.
        Assert.Equal(7, Interpreter.Run(Compile("fn main(): int { return 7; }"), [],
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null),
            Capability.None).AsI64);

    // ------------------------------------------------------------------ die Tabelle

    [Fact]
    public void Submodules_inherit_the_requirement_of_their_parent() =>
        // Sonst wäre jedes neue Untermodul eine stille Lücke: 'std.os.env' muss dasselbe kosten
        // wie 'std.os'.
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
        // 'std.ostrich' fängt mit 'std.os' an, ist aber ein anderes Modul. Ohne die
        // Punkt-Grenze im Vergleich wäre das eine falsche Ablehnung.
        Assert.Equal(Capability.None, CapabilityTable.RequiredForImport("std.ostrich"));

    [Fact]
    public void An_unknown_capability_name_is_rejected() =>
        // 'null' und nicht 'None': still weniger zu gewähren als verlangt wäre die gefährliche
        // Antwort — der Aufrufer soll melden.
        Assert.Null(CapabilityTable.Parse("file,quantum"));
}
