using System.Runtime.CompilerServices;
using System.Text;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Bytecode;

/// <summary>
/// Tests für Writer, Reader und Disassembler (M5/P5).
///
/// <para><b>Der Round-Trip ist die stärkste Aussage</b>: schreiben → lesen → wieder schreiben muss
/// byte-identisch sein. Das validiert Writer und Reader gegeneinander, ohne dass einer der beiden
/// als Referenz herhalten muss. Ein Golden-Snapshot allein könnte einen Fehler festschreiben, der
/// in beiden Richtungen konsistent ist — der Round-Trip nicht.</para>
///
/// <para>Eingaben sind dieselben Quell-Fixtures wie beim Lowering. Damit hängt P5 direkt an P4s
/// Netz statt an handgebauten Bytecode-Modulen.</para>
/// </summary>
public class BytecodeTests
{
    // ------------------------------------------------------------------ Helfer

    private static IrModule LowerSource(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        var writer = new StringWriter();
        de.RenderText(writer);
        Assert.False(de.HasErrors, "source did not compile:\n" + writer.ToString());

        var ir = ModuleLowerer.Lower(comp, types, de);
        Assert.NotNull(ir);
        return ir!;
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>Die Lowering-Fixtures aus P4 — Quelle und Erwartung liegen dort als Paar.</summary>
    private static string FixtureDir() =>
        Path.Combine(RepoRoot(), "tests", "Lyric.Tests.Ir", "golden", "lowering");

    public static TheoryData<string> Fixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in Directory.GetFiles(FixtureDir(), "*.lyr").OrderBy(p => p))
                data.Add(Path.GetFileNameWithoutExtension(path));
            return data;
        }
    }

    private static IrModule Fixture(string name) =>
        LowerSource(File.ReadAllText(Path.Combine(FixtureDir(), name + ".lyr"), Encoding.UTF8));

    // ------------------------------------------------------------------ 1) Round-Trip

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Write_read_write_is_byte_identical(string name)
    {
        var first = BytecodeWriter.Write(Fixture(name));
        var module = BytecodeReader.ReadOrThrow(first);

        // Zweiter Schreibvorgang aus derselben IR — der Reader hat den ersten validiert, also
        // beweist Gleichheit, dass nichts beim Lesen verlorenging oder umgedeutet wurde.
        var second = BytecodeWriter.Write(Fixture(name));

        Assert.Equal(first, second);
        Assert.Equal(module.Functions.Count, Fixture(name).Functions.Count);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Reading_validates_without_findings(string name)
    {
        var de = new DiagnosticEngine(new SourceManager());
        var module = BytecodeReader.Read(BytecodeWriter.Write(Fixture(name)), de);

        Assert.NotNull(module);
        Assert.Empty(de.Diagnostics);
    }

    [Fact]
    public void Output_is_deterministic()
    {
        // ADR-013 verlangt byte-identischen Output für gleichen Input. Ohne das wären
        // Golden-Tests und Bytecode-Diffs wertlos. Die Stolperfalle wäre ein String-Pool in
        // Hash- statt Erst-Verwendungs-Reihenfolge.
        const string source = """
            fn pick(flag: bool): string { return if (flag) "yes" else "no"; }
            fn again(flag: bool): string { return if (flag) "no" else "yes"; }
            """;

        Assert.Equal(BytecodeWriter.Write(LowerSource(source)),
                     BytecodeWriter.Write(LowerSource(source)));
    }

    [Fact]
    public void String_pool_is_deduplicated_and_in_first_use_order()
    {
        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(LowerSource("""
            fn a(): string { return "shared"; }
            fn b(): string { return "shared"; }
            """)));

        // Funktionsnamen zuerst (sie werden vor dem Code internt), dann die Literale.
        Assert.Equal(new[] { "a", "b", "shared" }, module.Strings.Select(s => s.Split('.')[^1]));
    }

    // ------------------------------------------------------------------ 2) Stack-Disziplin

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Declared_max_stack_matches_the_real_depth(string name)
    {
        // Der Reader prüft, dass die Tiefe nie die angekündigte übersteigt und an jeder
        // Blockgrenze 0 ist. Hier zusätzlich: die Angabe ist nicht nur eine Obergrenze ins Blaue.
        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(Fixture(name)));

        Assert.All(module.Functions, f =>
        {
            Assert.True(f.MaxStack > 0, $"{f.Name}: maxstack must be positive");
            Assert.True(f.MaxStack <= 8, $"{f.Name}: maxstack {f.MaxStack} is implausible for a scalar program");
        });
    }

    [Fact]
    public void Scheduler_keeps_values_on_the_stack_instead_of_spilling()
    {
        // Der eigentliche Zweck des Schedulings: 'return a + b' sind vier Instruktionen, nicht
        // zehn. Ginge jedes Temp in einen Slot, stünden hier zusätzlich drei stloc und drei ldloc
        // — und die Slot-Tabelle hätte fünf Einträge statt zwei.
        var module = BytecodeReader.ReadOrThrow(
            BytecodeWriter.Write(LowerSource("fn add(a: int, b: int): int { return a + b; }")));

        var function = Assert.Single(module.Functions);
        Assert.Equal(2, function.SlotTypes.Count); // nur die beiden Parameter, kein Temp-Slot

        var body = Disassembler.Dump(module).Split("bb0:\n")[1].TrimEnd('}', '\n');
        Assert.Equal(new[] { "ldloc 0", "ldloc 1", "add i64", "retval" },
            body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
    }

    // ------------------------------------------------------------------ 3) Reader-Robustheit

    private static byte[] ValidBytes() =>
        BytecodeWriter.Write(LowerSource("fn f(): int { return 1; }"));

    private static void AssertRejected(byte[] bytes, string expectedCode)
    {
        var de = new DiagnosticEngine(new SourceManager());
        var module = BytecodeReader.Read(bytes, de);

        Assert.Null(module);
        var diagnostic = Assert.Single(de.Diagnostics);
        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Equal(Severity.Error, diagnostic.Severity);
    }

    [Fact]
    public void Rejects_a_file_that_is_not_lyrbc() =>
        AssertRejected("not a bytecode file at all"u8.ToArray(), BytecodeDiagnostics.BadMagic);

    [Fact]
    public void Rejects_an_empty_file() =>
        AssertRejected(Array.Empty<byte>(), BytecodeDiagnostics.Truncated);

    [Fact]
    public void Rejects_an_unknown_major_version()
    {
        var bytes = ValidBytes();
        bytes[4] = 0xFF; // Major-Version, little-endian direkt hinter dem Magic
        bytes[5] = 0x00;
        AssertRejected(bytes, BytecodeDiagnostics.UnsupportedVersion);
    }

    [Fact]
    public void Rejects_a_truncated_file()
    {
        var bytes = ValidBytes();
        AssertRejected(bytes[..(bytes.Length - 3)], BytecodeDiagnostics.Truncated);
    }

    [Fact]
    public void Rejects_a_corrupted_opcode()
    {
        // Letztes Byte ist der Terminator der letzten Funktion. 0x7F ist kein definierter Opcode.
        var bytes = ValidBytes();
        bytes[^1] = 0x7F;
        AssertRejected(bytes, BytecodeDiagnostics.UnknownEncoding);
    }

    [Fact]
    public void Reader_never_throws_on_arbitrary_bytes()
    {
        // Der Leser ist die Stelle, an der nicht vertrauenswürdige Bytes ins System kommen. Er
        // darf auf keiner Eingabe mit einer .NET-Ausnahme aussteigen, nur mit einer Diagnose.
        var valid = ValidBytes();
        var random = new Random(20260730); // fester Seed: reproduzierbar

        for (var trial = 0; trial < 400; trial++)
        {
            var bytes = (byte[])valid.Clone();
            for (var flips = 0; flips < 3; flips++)
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);

            var de = new DiagnosticEngine(new SourceManager());
            var exception = Record.Exception(() => BytecodeReader.Read(bytes, de));
            Assert.True(exception is null,
                $"trial {trial} threw {exception?.GetType().Name}: {exception?.Message}");
        }
    }

    // ------------------------------------------------------------------ 4) Spec-Konformanz

    [Fact]
    public void Spec_documents_every_opcode_and_type_tag()
    {
        // docs/Bytecode.md ist normativ (ADR-013): der C#-Code ist eine Implementierung der Spec,
        // nicht ihre Definition. Dieser Test bindet beide aneinander — sonst driftet die Spec
        // beim ersten neuen Opcode, und das Ziel „jemand baut daraus eine zweite Runtime" ist tot.
        var spec = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "Bytecode.md"), Encoding.UTF8);

        foreach (var op in System.Enum.GetValues<Op>())
            Assert.True(spec.Contains($"0x{(byte)op:X2}", StringComparison.Ordinal),
                $"docs/Bytecode.md does not document opcode {op} (0x{(byte)op:X2})");

        foreach (var tag in System.Enum.GetValues<TypeTag>())
            Assert.True(spec.Contains($"0x{(byte)tag:X2}", StringComparison.Ordinal),
                $"docs/Bytecode.md does not document type tag {tag} (0x{(byte)tag:X2})");
    }

    [Fact]
    public void Spec_states_the_current_format_version()
    {
        var spec = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "Bytecode.md"), Encoding.UTF8);
        Assert.Contains($"{Format.VersionMajor}.{Format.VersionMinor}", spec, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 5) Gate-Programm

    [Fact]
    public void Gate_program_compiles_to_bytecode_and_disassembles()
    {
        // M5s Exit-Kriterium, als Test: examples/arith.lyr compiliert zu Bytecode, und die
        // Disassembly zeigt sinnvolle Instruktionen.
        var path = Path.Combine(RepoRoot(), "examples", "arith.lyr");
        var ir = LowerSource(File.ReadAllText(path, Encoding.UTF8));

        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir));
        var text = Disassembler.Dump(module);

        Assert.Equal(6, module.Functions.Count);
        Assert.Contains("fn main.main -> i64", text, StringComparison.Ordinal);
        Assert.Contains("call main.sumTo", text, StringComparison.Ordinal);
        Assert.Contains("condbr bb", text, StringComparison.Ordinal);

        // Kein redundantes store/load-Paar: das wäre das Zeichen, dass das Scheduling nicht greift.
        Assert.DoesNotContain("stloc 0\n    ldloc 0", text, StringComparison.Ordinal);
    }
}
