using System.Runtime.CompilerServices;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Bytecode;

/// <summary>
/// Sections 11 and 12 — what the writer emits, what the reader accepts, and what both refuse.
///
/// <para>The compiled half goes through the real pipeline with the standard library, because the
/// markers live in <c>std.core</c>. The rejection half builds files BY HAND from the
/// specification, the same reasoning as <c>SectionIds</c>: a reader validated only against its own
/// writer confirms itself.</para>
/// </summary>
public class AttributeSectionTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static IrModule Lower(string source)
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
        return ir!;
    }

    private static BytecodeModule Compile(string source) =>
        BytecodeReader.ReadOrThrow(BytecodeWriter.Write(Lower(source)));

    private const string Markers = """
        module app;
        import std.core { OnModule, OnType, OnFunction };

        pub struct Plugin :: [OnModule] { name: string, api: int }
        pub struct Component :: [OnType] { }
        pub struct System :: [OnFunction] { order: int = 0, label: string = "tick" }
        """;

    // ------------------------------------------------------------------ writer and reader

    [Fact]
    public void The_round_trip_of_an_attributed_module_is_byte_identical()
    {
        var source = Markers + """

            @Component
            pub struct Health { value: int, max: int }

            @System { order = -3 }
            pub fn tick(dt: float): void { }

            fn main(): int { return 0; }
            """;
        var first = BytecodeWriter.Write(Lower(source));
        var module = BytecodeReader.ReadOrThrow(first);

        Assert.Equal(2, module.Attributes.Count);
        // Reading changed nothing; a second write of the same IR is the same file.
        Assert.Equal(first, BytecodeWriter.Write(Lower(source)));
    }

    [Fact]
    public void A_row_carries_the_written_value_and_the_materialized_default()
    {
        var module = Compile(Markers + """

            @System { order = 10 }
            pub fn tick(dt: float): void { }

            fn main(): int { return 0; }
            """);

        var row = Assert.Single(module.Attributes);
        Assert.Equal(AttributeTargetKind.Function, row.TargetKind);
        Assert.Equal("app.tick", module.Functions[row.Target].Name);
        Assert.Equal("System", module.Types[row.Type].Name);

        // Complete row: the written 'order' and the default 'label', in field order.
        Assert.Equal(2, row.Values.Count);
        Assert.Equal(10, row.Values[0].AsInt);
        Assert.Equal("tick", row.Values[1].Text);
    }

    [Fact]
    public void A_negative_value_survives_the_trip()
    {
        var module = Compile(Markers + """

            @System { order = -3 }
            pub fn tick(dt: float): void { }

            fn main(): int { return 0; }
            """);

        Assert.Equal(-3, Assert.Single(module.Attributes).Values[0].AsInt);
    }

    [Fact]
    public void A_module_row_targets_index_zero()
    {
        var module = Compile("@Plugin { name = \"mymod\", api = 2 }\n"
            + Markers + "\nfn main(): int { return 0; }");

        var row = Assert.Single(module.Attributes);
        Assert.Equal(AttributeTargetKind.Module, row.TargetKind);
        Assert.Equal(0, row.Target);
        Assert.Equal("mymod", row.Values[0].Text);
        Assert.Equal(2, row.Values[1].AsInt);
    }

    [Fact]
    public void Names_cover_the_attribute_type_and_the_attributed_type_but_not_the_fieldless()
    {
        var module = Compile(Markers + """

            @Component
            pub struct Health { value: int, max: int }

            @System { order = 1 }
            pub fn tick(dt: float): void { }

            fn main(): int { return 0; }
            """);

        var byName = module.FieldNames
            .ToDictionary(entry => module.Types[entry.Type].Name, entry => entry.Names);

        Assert.Equal(["value", "max"], byName["Health"]);   // the attributed type
        Assert.Equal(["order", "label"], byName["System"]); // the attribute type
        // 'Component' is referenced but has no fields, so it has no entry to write.
        Assert.DoesNotContain("Component", byName.Keys);
    }

    [Fact]
    public void A_module_without_attributes_carries_neither_new_section()
    {
        var bytes = BytecodeWriter.Write(Lower("module app;\nfn main(): int { return 0; }"));

        Assert.DoesNotContain((byte)SectionId.Attributes, RawSectionIds(bytes));
        Assert.DoesNotContain((byte)SectionId.Names, RawSectionIds(bytes));
    }

    /// <summary>The reachability interplay: the attributed function is a root, its unattributed
    /// neighbour is pruned, and after the renumbering the row still names the right function — the
    /// test reads the NAME, because an off-by-one in the index would keep the numbers plausible.
    /// </summary>
    [Fact]
    public void Pruning_keeps_the_attributed_function_and_renumbers_the_row()
    {
        var module = Compile(Markers + """

            pub fn unreferenced(): int { return 1; }

            @System { order = 7 }
            pub fn kept(): int { return 2; }

            fn main(): int { return 0; }
            """);

        Assert.DoesNotContain(module.Functions, f => f.Name == "app.unreferenced");
        var row = Assert.Single(module.Attributes);
        Assert.Equal("app.kept", module.Functions[row.Target].Name);
    }

    [Fact]
    public void The_disassembly_shows_the_row_with_its_field_names()
    {
        var module = Compile(Markers + """

            @System { order = 5 }
            pub fn tick(dt: float): void { }

            fn main(): int { return 0; }
            """);

        var text = Disassembler.Dump(module);
        Assert.Contains("attribute @System {order = 5, label = \"tick\"} -> fn app.tick", text);
        Assert.Contains("names System(order, label)", text);
    }

    // ------------------------------------------------------------------ hand-built rejections

    /// <summary>A minimal module from the spec alone: one pooled string, one struct type with one
    /// i64 field, one empty function. The attribute section under test is appended by each
    /// case.</summary>
    private static List<byte> Prefix()
    {
        var bytes = new List<byte>();
        bytes.AddRange("LYRB"u8.ToArray());
        bytes.AddRange([3, 0, 2, 0]); // version 3.2

        // section 2 — strings: ["S", "f"]
        Section(bytes, 2, [2, 1, (byte)'S', 1, (byte)'f']);
        // section 3 — types: one struct (kind 3), nameIndex 0, one i64 field
        Section(bytes, 3, [1, 0, 3, 1, (byte)TypeTag.I64]);
        // section 5 — functions: name f, 0 params, void, 0 slots, 0 stack, 1 block at 0, code [ret]
        Section(bytes, 5, [1, 1, 0, (byte)TypeTag.Void, 0, 0, 1, 0, 1, 0x41]);
        return bytes;
    }

    private static void Section(List<byte> bytes, byte id, byte[] payload)
    {
        bytes.Add(id);
        bytes.Add((byte)payload.Length); // all payloads here are short, one leb group
        bytes.AddRange(payload);
    }

    private static string RejectionCode(byte[] attributesPayload)
    {
        var bytes = Prefix();
        Section(bytes, 11, attributesPayload);
        var error = Assert.Throws<MalformedBytecodeException>(
            () => BytecodeReader.ReadOrThrow(bytes.ToArray()));
        return error.Code;
    }

    // Row shape: targetKind, target, type, valueCount, values…; the valid row for reference is
    // [0, 0, 0, 1, I64, value].

    [Fact]
    public void Rejects_an_unknown_target_kind() =>
        Assert.Equal(BytecodeDiagnostics.UnknownEncoding,
            RejectionCode([1, 3, 0, 0, 1, (byte)TypeTag.I64, 9]));

    [Fact]
    public void Rejects_a_function_target_out_of_range() =>
        Assert.Equal(BytecodeDiagnostics.IndexOutOfRange,
            RejectionCode([1, 0, 7, 0, 1, (byte)TypeTag.I64, 9]));

    [Fact]
    public void Rejects_a_type_target_out_of_range() =>
        Assert.Equal(BytecodeDiagnostics.IndexOutOfRange,
            RejectionCode([1, 1, 7, 0, 1, (byte)TypeTag.I64, 9]));

    [Fact]
    public void Rejects_a_module_target_with_a_nonzero_index() =>
        Assert.Equal(BytecodeDiagnostics.UnknownEncoding,
            RejectionCode([1, 2, 1, 0, 1, (byte)TypeTag.I64, 9]));

    [Fact]
    public void Rejects_an_attribute_type_out_of_range() =>
        Assert.Equal(BytecodeDiagnostics.IndexOutOfRange,
            RejectionCode([1, 0, 0, 7, 1, (byte)TypeTag.I64, 9]));

    [Fact]
    public void Rejects_a_value_count_that_misses_the_layout() =>
        Assert.Equal(BytecodeDiagnostics.UnknownEncoding,
            RejectionCode([1, 0, 0, 0, 0]));

    [Fact]
    public void Rejects_a_value_tag_that_misses_the_field() =>
        Assert.Equal(BytecodeDiagnostics.UnknownEncoding,
            RejectionCode([1, 0, 0, 0, 1, (byte)TypeTag.Bool, 1]));

    [Fact]
    public void Rejects_the_same_pair_twice() =>
        Assert.Equal(BytecodeDiagnostics.UnknownEncoding,
            RejectionCode([2, 0, 0, 0, 1, (byte)TypeTag.I64, 9, 0, 0, 0, 1, (byte)TypeTag.I64, 9]));

    [Fact]
    public void Rejects_a_string_value_outside_the_pool() =>
        Assert.Equal(BytecodeDiagnostics.IndexOutOfRange,
            RejectionCode([1, 0, 0, 0, 1, (byte)TypeTag.String, 9]));

    [Fact]
    public void Rejects_a_names_entry_with_the_wrong_count()
    {
        var bytes = Prefix();
        // section 12: one entry, type 0, two names for one field
        Section(bytes, 12, [1, 0, 2, 1, (byte)'a', 1, (byte)'b']);
        var error = Assert.Throws<MalformedBytecodeException>(
            () => BytecodeReader.ReadOrThrow(bytes.ToArray()));
        Assert.Equal(BytecodeDiagnostics.UnknownEncoding, error.Code);
    }

    [Fact]
    public void Rejects_a_names_entry_for_a_type_out_of_range()
    {
        var bytes = Prefix();
        Section(bytes, 12, [1, 7, 1, 1, (byte)'a']);
        var error = Assert.Throws<MalformedBytecodeException>(
            () => BytecodeReader.ReadOrThrow(bytes.ToArray()));
        Assert.Equal(BytecodeDiagnostics.IndexOutOfRange, error.Code);
    }

    /// <summary>The value-tag check presupposes the attribute type is a STRUCT; a class layout in
    /// that position is refused before any value is read.</summary>
    [Fact]
    public void Rejects_an_attribute_type_that_is_not_a_struct()
    {
        var bytes = new List<byte>();
        bytes.AddRange("LYRB"u8.ToArray());
        bytes.AddRange([3, 0, 2, 0]);
        Section(bytes, 2, [2, 1, (byte)'S', 1, (byte)'f']);
        // kind 0: a CLASS layout, same shape, no value semantics
        Section(bytes, 3, [1, 0, 0, 1, (byte)TypeTag.I64]);
        Section(bytes, 5, [1, 1, 0, (byte)TypeTag.Void, 0, 0, 1, 0, 1, 0x41]);
        Section(bytes, 11, [1, 0, 0, 0, 1, (byte)TypeTag.I64, 9]);

        var error = Assert.Throws<MalformedBytecodeException>(
            () => BytecodeReader.ReadOrThrow(bytes.ToArray()));
        Assert.Equal(BytecodeDiagnostics.UnknownEncoding, error.Code);
    }

    /// <summary>The section ids in file order, read straight off the byte stream — deliberately
    /// not through the reader, the same reasoning as <c>SectionIds</c> in
    /// <see cref="BytecodeTests"/>.</summary>
    private static List<byte> RawSectionIds(byte[] bytes)
    {
        var ids = new List<byte>();
        var at = 8; // magic + two u16 versions
        while (at < bytes.Length)
        {
            ids.Add(bytes[at++]);
            var length = 0;
            var shift = 0;
            while (true)
            {
                var b = bytes[at++];
                length |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            at += length;
        }
        return ids;
    }
}
