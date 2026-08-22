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
/// Section 14 — the name of the <c>opaque type</c> a field was declared with.
///
/// <para>The one place in the pipeline where an opaque alias leaves a trace. Everywhere else it
/// IS its underlying type by design: <c>opaque type Entity = int</c> is an <c>i64</c> in every
/// layout and every instruction, which is what makes a handle free to pass. The consequence is
/// what these tests are about — a host reading the shape of an attributed class saw a number
/// where the source wrote a handle, and a handle is exactly what must not be written to a save
/// file.</para>
///
/// <para>Hand-built rejections beside the compiled round trips, the same reasoning as the
/// neighbouring section tests: a reader validated only against its own writer confirms itself.
/// </para>
/// </summary>
public class OpaqueFieldSectionTests
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

    private static byte[] Write(string source) => BytecodeWriter.Write(Lower(source));

    private static BytecodeModule Compile(string source) =>
        BytecodeReader.ReadOrThrow(Write(source));

    private const string Header = """
        module app;
        import std.core { OnType };

        pub opaque type Entity = int;

        pub struct Saved :: [OnType] { version: int = 1 }
        """;

    /// <summary>The names of one type by its NAME, so a test does not depend on where in the
    /// table the standard library left it.</summary>
    private static IReadOnlyList<string>? OpaqueOf(BytecodeModule module, string type)
    {
        var index = -1;
        for (var i = 0; i < module.Types.Count; i++)
            if (module.Types[i].Name == type) index = i;
        Assert.True(index >= 0, $"no type '{type}' in the table");
        return module.OpaqueFields.FirstOrDefault(o => o.Type == index)?.Names;
    }

    // ------------------------------------------------------------------ writer and reader

    [Fact]
    public void An_opaque_field_carries_its_name_and_an_ordinary_one_does_not()
    {
        var module = Compile(Header + """

            @Saved
            pub class Holder { hero: Entity = 0 as Entity, stage: int = 0 }
            """);

        Assert.Equal(["Entity", ""], OpaqueOf(module, "Holder"));

        // What the finding was: the field TYPES are indistinguishable, and stay so — this section
        // adds a name beside them rather than a type.
        var holder = module.Types.Single(t => t.Name == "Holder");
        Assert.Equal([TypeTag.I64, TypeTag.I64], holder.FieldTypes.Select(f => f.Tag));
    }

    [Fact]
    public void The_name_is_the_leaf_through_an_array_and_an_optional()
    {
        // A list of handles is as unsaveable as a handle, and the field type still says which of
        // the two it is.
        var module = Compile(Header + """

            @Saved
            pub class Holder { party: Entity[] = [], mount: ?Entity = null }
            """);

        Assert.Equal(["Entity", "Entity"], OpaqueOf(module, "Holder"));

        var holder = module.Types.Single(t => t.Name == "Holder");
        Assert.Equal([TypeTag.Array, TypeTag.Optional], holder.FieldTypes.Select(f => f.Tag));
    }

    [Fact]
    public void A_transparent_alias_answers_with_what_it_names()
    {
        // 'type Slot = Entity' is a NAME for the opaque type, not a second one, so the answer is
        // the type that is actually distinct. An alias of a primitive answers nothing at all.
        var module = Compile(Header + """

            pub type Slot = Entity;
            pub type Count = int;

            @Saved
            pub class Holder { slot: Slot = 0 as Entity, n: Count = 0 }
            """);

        Assert.Equal(["Entity", ""], OpaqueOf(module, "Holder"));
    }

    [Fact]
    public void A_module_without_an_opaque_field_carries_no_section()
    {
        var bytes = Write(Header + """

            @Saved
            pub class Holder { stage: int = 0 }
            """);

        Assert.DoesNotContain((byte)SectionId.OpaqueFields, RawSectionIds(bytes));
        Assert.Empty(BytecodeReader.ReadOrThrow(bytes).OpaqueFields);
    }

    [Fact]
    public void The_section_stands_last_so_the_ids_ascend()
    {
        // Not cosmetic: sections appear in ascending id order, and a reader rejects one that does
        // not. The Names section it belongs beside carries id 12.
        var bytes = Write(Header + """

            @Saved
            pub class Holder { hero: Entity = 0 as Entity }
            """);

        var ids = RawSectionIds(bytes);
        Assert.Equal(ids.OrderBy(id => id), ids);
        Assert.Contains((byte)SectionId.Names, ids);
        Assert.Equal((byte)SectionId.OpaqueFields, ids[^1]);
    }

    [Fact]
    public void The_round_trip_is_byte_identical()
    {
        var source = Header + """

            @Saved
            pub class Holder { hero: Entity = 0 as Entity, stage: int = 0 }
            """;

        var first = Write(source);
        Assert.Equal(first, BytecodeWriter.Write(Lower(source)));
    }

    // ------------------------------------------------------------------ hand-built rejections

    /// <summary>A minimal module from the spec alone: one pooled string, one struct type with one
    /// i64 field, one empty function — the same prefix the neighbouring section tests use.
    /// </summary>
    private static List<byte> Prefix()
    {
        var bytes = new List<byte>();
        bytes.AddRange("LYRB"u8.ToArray());
        bytes.AddRange([3, 0, 5, 0]); // version 3.5

        Section(bytes, 2, [2, 1, (byte)'S', 1, (byte)'f']);
        Section(bytes, 3, [1, 0, 3, 1, (byte)TypeTag.I64]);
        Section(bytes, 5, [1, 1, 0, (byte)TypeTag.Void, 0, 0, 1, 0, 1, 0x41]);
        return bytes;
    }

    private static void Section(List<byte> bytes, byte id, byte[] payload)
    {
        bytes.Add(id);
        bytes.Add((byte)payload.Length);
        bytes.AddRange(payload);
    }

    private static string RejectionCode(byte[] payload)
    {
        var bytes = Prefix();
        Section(bytes, (byte)SectionId.OpaqueFields, payload);
        return Assert.Throws<MalformedBytecodeException>(
            () => BytecodeReader.ReadOrThrow(bytes.ToArray())).Code;
    }

    // Entry shape: type, nameCount, names…; the valid entry for reference is [1, 0, 1, 1, 'E'].

    [Fact]
    public void Accepts_the_valid_entry_the_rejections_are_measured_against()
    {
        var bytes = Prefix();
        Section(bytes, (byte)SectionId.OpaqueFields, [1, 0, 1, 1, (byte)'E']);
        var module = BytecodeReader.ReadOrThrow(bytes.ToArray());
        Assert.Equal(["E"], Assert.Single(module.OpaqueFields).Names);
    }

    [Fact]
    public void Rejects_a_type_out_of_range() =>
        Assert.Equal(BytecodeDiagnostics.IndexOutOfRange,
            RejectionCode([1, 7, 1, 1, (byte)'E']));

    [Fact]
    public void Rejects_a_name_count_that_misses_the_layout() =>
        Assert.Equal(BytecodeDiagnostics.UnknownEncoding,
            RejectionCode([1, 0, 2, 1, (byte)'E', 1, (byte)'F']));

    [Fact]
    public void Rejects_the_same_type_twice() =>
        Assert.Equal(BytecodeDiagnostics.UnknownEncoding,
            RejectionCode([2, 0, 1, 1, (byte)'E', 0, 1, 1, (byte)'E']));

    /// <summary>The section ids in file order, read straight off the byte stream rather than
    /// through the reader — a reader that skipped a section would hide it.</summary>
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
