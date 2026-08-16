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
/// Tests for the writer, the reader and the disassembler.
///
/// <para>THE ROUND TRIP IS THE STRONGEST STATEMENT: write, read, write again has to be byte-identical.
/// That validates writer and reader against each other without either serving as the reference. A
/// golden snapshot alone could fix a fault that is consistent in both directions; the round trip cannot.
/// </para>
///
/// <para>The inputs are the same source fixtures as for the lowering, so this hangs on the lowering's
/// net rather than on hand-built bytecode modules.</para>
/// </summary>
public class BytecodeTests
{
    // ------------------------------------------------------------------ helpers

    private static IrModule LowerSource(string source) => LowerSource(source, out _);

    private static IrModule LowerSource(string source, out SourceManager sources)
    {
        var sm = new SourceManager();
        sources = sm;
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        var writer = new StringWriter();
        de.RenderText(writer);
        Assert.False(de.HasErrors, "source did not compile:\n" + writer.ToString());

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);
        return ir!;
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>The lowering fixtures: source and expectation lie there as a pair.</summary>
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

    // ------------------------------------------------------------------ 1) the round trip

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Write_read_write_is_byte_identical(string name)
    {
        var first = BytecodeWriter.Write(Fixture(name));
        var module = BytecodeReader.ReadOrThrow(first);

        // A second write from the same IR: the reader validated the first, so equality proves that nothing
        // was lost or reinterpreted while reading.
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
        // The same input has to give byte-identical output. Without that, golden tests and bytecode diffs
        // would be worthless. The trap would be a string pool in hash order rather than first-use order.
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

        // Function names first, since they are interned before the code, then the literals.
        Assert.Equal(new[] { "a", "b", "shared" }, module.Strings.Select(s => s.Split('.')[^1]));
    }

    // ------------------------------------------------------------------ 2) stack discipline

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Declared_max_stack_matches_the_real_depth(string name)
    {
        // The reader checks that the depth never exceeds the announced one and is 0 at every block
        // boundary. Additionally here: the figure is not just an upper bound plucked from the air.
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
        // The actual purpose of the scheduling: 'return a + b' is four instructions rather than ten. If
        // every temp went into a slot, three stloc and three ldloc would stand here as well, and the slot
        // table would have five entries instead of two.
        var module = BytecodeReader.ReadOrThrow(
            BytecodeWriter.Write(LowerSource("fn add(a: int, b: int): int { return a + b; }")));

        var function = Assert.Single(module.Functions);
        Assert.Equal(2, function.SlotTypes.Count); // the two parameters only, with no temp slot

        var body = Disassembler.Dump(module).Split("bb0:\n")[1].TrimEnd('}', '\n');
        Assert.Equal(new[] { "ldloc 0", "ldloc 1", "add i64", "retval" },
            body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
    }

    // ------------------------------------------------------------------ 3) reader robustness

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
        bytes[4] = 0xFF; // the major version, little-endian directly behind the magic
        bytes[5] = 0x00;
        AssertRejected(bytes, BytecodeDiagnostics.UnsupportedVersion);
    }

    [Fact]
    public void Skips_a_section_with_an_unknown_id()
    {
        // The forward compatibility a new minor version rests on: "a new minor version may only add
        // skippable sections". Nothing had ever WRITTEN an unknown section, so nothing had ever
        // skipped one, and the reader rejected the payload it was supposed to step over.
        var bytes = ValidBytes();

        // Id 11 ascends past every id the writer emits, so appending keeps the file well formed.
        var extended = bytes.Concat(new byte[] { 11, 2, 0xAA, 0xBB }).ToArray();

        var de = new DiagnosticEngine(new SourceManager());
        var module = BytecodeReader.Read(extended, de);

        Assert.NotNull(module);
        Assert.Empty(de.Diagnostics);

        // The skipped bytes changed nothing about what was read.
        Assert.Equal(BytecodeReader.ReadOrThrow(bytes).Functions.Count, module!.Functions.Count);
    }

    [Fact]
    public void Tolerates_an_unknown_minor_version()
    {
        // The other half of the same promise: the major decides whether a file is readable, the
        // minor only says which skippable sections it may contain.
        var bytes = ValidBytes();
        bytes[6] = 0xFF; // the minor version, little-endian behind the major
        bytes[7] = 0x00;

        var de = new DiagnosticEngine(new SourceManager());

        Assert.NotNull(BytecodeReader.Read(bytes, de));
        Assert.Empty(de.Diagnostics);
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
        // The last byte is the terminator of the last function. 0x7F is no defined opcode.
        var bytes = ValidBytes();
        bytes[^1] = 0x7F;
        AssertRejected(bytes, BytecodeDiagnostics.UnknownEncoding);
    }

    [Fact]
    public void Reader_never_throws_on_arbitrary_bytes()
    {
        // The reader is where untrusted bytes enter the system. It must not bail out with a .NET exception
        // on any input, only with a diagnostic.
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

    [Theory]
    // §5: "'add' through 'rem' require a numeric type". The rule was normative and enforced nowhere,
    // and that is what turned a lowering bug into silently wrong output: a release build runs no IR
    // verifier, so 'add string' passed 'lyrvm verify' and the interpreter added the two references
    // as integers. The tag is patched in a module the compiler itself produced, because a
    // hand-assembled one would prove nothing about the shape the writer emits.
    [InlineData("fn f(): int { return 1 + 1; }", Op.Add, TypeTag.String)]
    [InlineData("fn f(): int { return 1 + 1; }", Op.Add, TypeTag.Bool)]
    // '<' is numeric too; '==' is not, so 'eq string' must stay accepted (checked below).
    [InlineData("fn f(): bool { return 1 < 2; }", Op.Lt, TypeTag.String)]
    // The shift and bitwise family is stricter still: integers only, no floats.
    [InlineData("fn f(): int { return 1 << 2; }", Op.Shl, TypeTag.F64)]
    public void Rejects_an_arithmetic_opcode_with_a_type_it_does_not_accept(
        string source, Op opcode, TypeTag tag) =>
        AssertRejected(WithPatchedTag(source, opcode, tag), BytecodeDiagnostics.UnknownEncoding);

    [Fact]
    public void Accepts_equality_on_the_non_numeric_types_that_have_it() =>
        // The counter-check to the theory above. 'eq' and 'ne' hold for bool, char and string as
        // well, and a check that rejected those would break every string comparison in the stdlib.
        Assert.NotNull(BytecodeReader.ReadOrThrow(
            BytecodeWriter.Write(LowerSource("fn f(): bool { return \"a\" == \"b\"; }"))));

    /// <summary>Compiles the source and overwrites the type tag of the first
    /// <paramref name="opcode"/> in the last function's code.</summary>
    private static byte[] WithPatchedTag(string source, Op opcode, TypeTag tag)
    {
        var bytes = BytecodeWriter.Write(LowerSource(source));
        var code = BytecodeReader.ReadOrThrow(bytes).Functions[^1].Code;

        // The tag sits one byte behind the opcode, and the code block appears verbatim in the file.
        var instruction = CodeDecoder.Decode(code).First(i => i.Opcode == opcode);
        var start = IndexOf(bytes, code);
        Assert.True(start >= 0, "the function's code was not found in the file");

        bytes[start + instruction.Offset + 1] = (byte)tag;
        return bytes;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }

    // ------------------------------------------------------------------ 4) conformance to the spec

    [Fact]
    public void Spec_documents_every_opcode_and_type_tag()
    {
        // docs/Bytecode.md is normative: the C# code is an implementation of the specification rather than
        // its definition. This test binds the two together; otherwise the specification drifts at the first
        // new opcode and the goal "someone builds a second runtime from it" is dead.
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

    /// <summary>
    /// The section ids in file order, read straight off the byte stream: magic, the two version
    /// numbers, then a sequence of id, <c>uleb128</c> length and payload.
    ///
    /// <para>Deliberately not through <see cref="BytecodeReader"/>. The rule under test is one a
    /// second runtime derives from the specification alone, and a reader sharing the writer's idea
    /// of the order would only confirm itself.</para>
    /// </summary>
    private static List<byte> SectionIds(byte[] bytes)
    {
        var ids = new List<byte>();
        var at = Format.Magic.Length + sizeof(ushort) + sizeof(ushort);

        while (at < bytes.Length)
        {
            ids.Add(bytes[at++]);

            var length = 0;
            var shift = 0;
            while (true)
            {
                var group = bytes[at++];
                length |= (group & 0x7F) << shift;
                if ((group & 0x80) == 0) break;
                shift += 7;
            }

            at += length;
        }

        return ids;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Section_ids_ascend_strictly(string name)
    {
        // Ascending AND at most once, which is what lets a reader work in a single pass. Sorting
        // and deduplicating states both in one comparison.
        var ids = SectionIds(BytecodeWriter.Write(Fixture(name)));
        Assert.Equal(ids.Order().Distinct(), ids);
    }

    // ------------------------------------------------------------------ 5) the source map

    private const string TwoLineProgram = """
        fn f(): int {
            let a = 1;
            let b = 2;
            return a + b;
        }
        """;

    [Fact]
    public void Without_sources_the_output_is_unchanged()
    {
        // The section is strippable, and this is what that has to mean: a build without it produces
        // exactly the bytes it produced before the section existed. If the two ever differ, "strip
        // it" stops being a decision a user can make without changing anything else.
        var withMap = BytecodeWriter.Write(LowerSource(TwoLineProgram, out var sources),
            new SourceMapContext(sources, Directory.GetCurrentDirectory()));
        var without = BytecodeWriter.Write(LowerSource(TwoLineProgram));

        Assert.NotEqual(withMap, without);
        Assert.DoesNotContain((byte)SectionId.SourceMap, SectionIds(without));
        Assert.Contains((byte)SectionId.SourceMap, SectionIds(withMap));
    }

    [Fact]
    public void A_module_with_a_source_map_still_loads_and_stays_in_order()
    {
        // Two statements on two lines, so the map holds more than one row and the section is not
        // trivially empty.
        var bytes = BytecodeWriter.Write(LowerSource(TwoLineProgram, out var sources),
            new SourceMapContext(sources, Directory.GetCurrentDirectory()));

        var ids = SectionIds(bytes);
        Assert.Contains((byte)SectionId.SourceMap, ids);
        Assert.Equal(ids.Order().Distinct(), ids);

        // The reader does not parse section 6 yet. That it loads anyway is the point: the section
        // is skippable, which is what lets an older runtime read a newer module.
        var de = new DiagnosticEngine(new SourceManager());
        Assert.NotNull(BytecodeReader.Read(bytes, de));
        Assert.Empty(de.Diagnostics);
    }

    [Fact]
    public void The_source_map_names_the_lines_the_code_came_from()
    {
        // The first test of the CONTENT rather than the shape. A builder that consistently records
        // the line before is green on every other test in this section.
        var bytes = BytecodeWriter.Write(LowerSource(TwoLineProgram, out var sources),
            new SourceMapContext(sources, Directory.GetCurrentDirectory()));

        var map = BytecodeReader.ReadOrThrow(bytes).SourceMap;
        Assert.NotNull(map);

        var rows = Assert.Single(map!.Functions);

        // Four lines carry code: 'let a' on 2, 'let b' on 3, 'return a + b' on 4. Line 1 is the
        // signature and produces none.
        Assert.Equal([2, 3, 4], rows.Select(r => r.Line).Distinct());

        // Every row lies inside the code and they ascend, which is what Locate bisects over.
        Assert.Equal(rows.OrderBy(r => r.Offset), rows);

        // Resolving the first row's own offset gives its line back.
        var position = map.Locate(0, rows[0].Offset);
        Assert.NotNull(position);
        Assert.Equal(2, position!.Value.Line);

        // A file compiled from a virtual name keeps it; nothing absolute reaches the pool.
        Assert.Equal("test.lyr", Assert.Single(map.Files));
    }

    [Fact]
    public void The_source_map_is_deterministic()
    {
        // Same input, same bytes — the promise of section 1. The trap would be a file table in hash
        // order, the same one the string pool avoids by interning in first-use order.
        var first = BytecodeWriter.Write(LowerSource(TwoLineProgram, out var a),
            new SourceMapContext(a, Directory.GetCurrentDirectory()));
        var second = BytecodeWriter.Write(LowerSource(TwoLineProgram, out var b),
            new SourceMapContext(b, Directory.GetCurrentDirectory()));

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_module_with_globals_and_handlers_writes_both_in_order()
    {
        // The two highest ids are the two sections the writer emits last, each on a condition of its
        // own: a global binding and a protected region. A module carrying only one of them says
        // nothing about their relative order, and no fixture above carries both.
        var bytes = BytecodeWriter.Write(LowerSource("""
            let LIMIT = 100;

            pub class TooMuch :: [Throwable] {
                asked: int,

                fn message(): string { return "too much"; }
            }

            fn take(amount: int) throws TooMuch {
                if (amount > LIMIT) { throw TooMuch { asked = amount }; }
            }

            fn main(): int {
                try { take(200); } catch (e: TooMuch) { return e.asked; }
                return LIMIT;
            }
            """));

        var ids = SectionIds(bytes);

        // Without these two the test would stay green while measuring nothing: a module that grew
        // neither section satisfies the ordering trivially.
        Assert.Contains((byte)SectionId.Handlers, ids);
        Assert.Contains((byte)SectionId.Globals, ids);
        Assert.Equal(ids.Order().Distinct(), ids);

        // The reader is what rejects a misordered file, so it has the last word here.
        var de = new DiagnosticEngine(new SourceManager());
        Assert.NotNull(BytecodeReader.Read(bytes, de));
        Assert.Empty(de.Diagnostics);
    }

    // ------------------------------------------------------------------ 5) the gate program

    [Fact]
    public void Gate_program_compiles_to_bytecode_and_disassembles()
    {
        // examples/arith.lyr compiles to bytecode and the disassembly shows meaningful instructions.
        var path = Path.Combine(RepoRoot(), "examples", "arith.lyr");
        var ir = LowerSource(File.ReadAllText(path, Encoding.UTF8));

        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir));
        var text = Disassembler.Dump(module);

        Assert.Equal(6, module.Functions.Count);
        Assert.Contains("fn main.main -> i64", text, StringComparison.Ordinal);
        Assert.Contains("call main.sumTo", text, StringComparison.Ordinal);
        Assert.Contains("condbr bb", text, StringComparison.Ordinal);

        // No redundant store/load pair: that would be the sign that the scheduling does not apply.
        Assert.DoesNotContain("stloc 0\n    ldloc 0", text, StringComparison.Ordinal);
    }
}
