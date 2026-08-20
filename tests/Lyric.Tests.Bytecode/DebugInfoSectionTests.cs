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
/// Section 13 (DebugInfo, format 3.3) — slot names per function: what the writer emits, what the
/// reader accepts, and what it refuses.
///
/// <para>The rejection half builds section payloads BY HAND from the specification, appended to a
/// module written WITHOUT the section — a reader validated only against its own writer confirms
/// itself.</para>
/// </summary>
public class DebugInfoSectionTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>Lowers WITHOUT the optimizations by default: a small test program inlines away
    /// completely, leaving only <c>__inl_*</c> slots — and unoptimized is also the shape a
    /// debug compile produces.</summary>
    private static IrModule Lower(string source, bool optimize = false)
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

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true, optimize: optimize);
        Assert.NotNull(ir);
        return ir!;
    }

    private const string Program = """
        module app;

        fn add(a: int, b: int): int {
            let sum = a + b;
            return sum;
        }

        fn main(): int { return add(1, 2); }
        """;

    // ------------------------------------------------------------------ writer and reader

    [Fact]
    public void Parameters_and_locals_come_back_named()
    {
        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(Lower(Program)));

        Assert.NotNull(module.SlotNames);
        var add = IndexOf(module, "app.add");
        var names = module.SlotNames![add];

        // The list is full length: the position IS the slot index.
        Assert.Equal(module.Functions[add].SlotTypes.Count, names.Count);
        Assert.Equal("a", names[0]);
        Assert.Equal("b", names[1]);
        Assert.Contains("sum", names);
    }

    [Fact]
    public void A_compiler_created_slot_carries_the_empty_string()
    {
        // The if EXPRESSION carries its value across a block boundary through a synthetic local
        // ('$if…' in the IR). The section says "no person bound this" with the empty string.
        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(Lower("""
            module app;

            fn pick(c: bool): int {
                let x = if (c) 1 else 2;
                return x;
            }

            fn main(): int { return pick(true); }
            """)));

        var names = module.SlotNames![IndexOf(module, "app.pick")];
        Assert.Equal("c", names[0]);
        Assert.Contains("x", names);
        Assert.Contains("", names); // the synthetic carrier of the if value
    }

    [Fact]
    public void The_receiver_slot_is_named_this()
    {
        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(Lower("""
            module app;

            pub class Counter {
                value: int = 0,
                fn bump(): void { this.value = this.value + 1; }
            }

            fn main(): int {
                let c = Counter {};
                c.bump();
                return c.value;
            }
            """)));

        var bump = IndexOf(module, "app.Counter.bump");
        Assert.Equal("this", module.SlotNames![bump][0]);
    }

    [Fact]
    public void Stripping_removes_the_section_and_the_extra_names()
    {
        var ir = Lower(Program);
        var full = BytecodeWriter.Write(ir);
        var stripped = BytecodeWriter.Write(ir, debugInfo: false);

        Assert.Contains((byte)SectionId.DebugInfo, SectionIds(full));
        Assert.DoesNotContain((byte)SectionId.DebugInfo, SectionIds(stripped));
        Assert.Null(BytecodeReader.ReadOrThrow(stripped).SlotNames);

        // The stripped module still loads and still runs the same program.
        Assert.Equal(BytecodeReader.ReadOrThrow(full).Functions.Count,
            BytecodeReader.ReadOrThrow(stripped).Functions.Count);
    }

    [Fact]
    public void Names_covers_a_plain_struct_when_debug_info_is_on()
    {
        // Since 3.3 the Names section is a floor, not a ceiling: with debug info on, a type
        // nobody attributed still gets its field names — a debugger expanding a Vec2 needs them.
        var ir = Lower("""
            module app;

            pub struct Vec2 { x: float, y: float }

            fn main(): int {
                let v = Vec2 { x = 1.0, y = 2.0 };
                return if (v.x < v.y) 0 else 1;
            }
            """);

        var withDebug = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir));
        var vec = withDebug.FieldNames.FirstOrDefault(
            n => withDebug.Types[n.Type].Name == "Vec2");
        Assert.NotNull(vec);
        Assert.Equal(["x", "y"], vec!.Names);

        // Stripped, the 3.2 rule returns: no attribute row, no entry.
        var stripped = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir, debugInfo: false));
        Assert.DoesNotContain(stripped.FieldNames,
            n => stripped.Types[n.Type].Name == "Vec2");
    }

    [Fact]
    public void The_write_is_deterministic()
    {
        Assert.Equal(BytecodeWriter.Write(Lower(Program)), BytecodeWriter.Write(Lower(Program)));
    }

    [Fact]
    public void An_inlined_slot_is_not_a_name()
    {
        // With the optimizations ON, the tiny program inlines completely: main's slots are all
        // '__inl_*' copies, which the section treats as compiler-created. Nothing is left to
        // name, so the section is honestly absent rather than full of empty strings.
        var module = BytecodeReader.ReadOrThrow(
            BytecodeWriter.Write(Lower(Program, optimize: true)));

        Assert.Null(module.SlotNames);
    }

    // ------------------------------------------------------------------ reader rejections

    [Fact]
    public void Rejects_a_function_count_that_differs_from_the_function_section()
    {
        // functionCount 0 against a module that has functions.
        var bytes = Append(StrippedProgram(), [0]);
        AssertRejected(bytes, "debug info covers 0 function(s)");
    }

    [Fact]
    public void Rejects_a_partial_name_list()
    {
        var stripped = StrippedProgram();
        var functionCount = BytecodeReader.ReadOrThrow(stripped).Functions.Count;

        // One name for the first function, whatever its slot count is (the fixtures give every
        // function at least two slots); 0 for the rest.
        var payload = new List<byte> { (byte)functionCount, 1, 0 };
        for (var i = 1; i < functionCount; i++) payload.Add(0);

        AssertRejected(Append(stripped, payload.ToArray()), "name(s) for");
    }

    [Fact]
    public void Rejects_a_string_index_outside_the_pool()
    {
        var stripped = StrippedProgram();
        var module = BytecodeReader.ReadOrThrow(stripped);

        var payload = new List<byte> { (byte)module.Functions.Count };
        for (var f = 0; f < module.Functions.Count; f++)
        {
            if (f > 0) { payload.Add(0); continue; }

            // Full length for function 0, but every name points past the pool.
            var slots = module.Functions[0].SlotTypes.Count;
            payload.Add((byte)slots);
            for (var s = 0; s < slots; s++) AppendULeb(payload, (ulong)module.Strings.Count);
        }

        AssertRejected(Append(stripped, payload.ToArray()), "the pool holds");
    }

    [Fact]
    public void Accepts_a_function_that_says_nothing()
    {
        // nameCount 0 for every function is a valid section: the module says "I carry the
        // section, and no function names anything".
        var stripped = StrippedProgram();
        var functionCount = BytecodeReader.ReadOrThrow(stripped).Functions.Count;

        var payload = new List<byte>();
        AppendULeb(payload, (ulong)functionCount);
        for (var i = 0; i < functionCount; i++) payload.Add(0);
        payload.Add(0); // globalNameCount

        var module = BytecodeReader.ReadOrThrow(Append(stripped, payload.ToArray()));
        Assert.NotNull(module.SlotNames);
        Assert.All(module.SlotNames!, names => Assert.Empty(names));
    }

    [Fact]
    public void Global_slots_come_back_named()
    {
        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(Lower("""
            module app;

            let limit = 100;
            let greeting = "hi";

            fn main(): int { return limit; }
            """)));

        Assert.Equal(module.Globals.Count, module.GlobalNames.Count);
        Assert.Contains("limit", module.GlobalNames);
        Assert.Contains("greeting", module.GlobalNames);
    }

    [Fact]
    public void Rejects_a_global_name_count_that_differs_from_the_globals()
    {
        var stripped = StrippedProgram(); // no globals in the fixture
        var functionCount = BytecodeReader.ReadOrThrow(stripped).Functions.Count;

        var payload = new List<byte>();
        AppendULeb(payload, (ulong)functionCount);
        for (var i = 0; i < functionCount; i++) payload.Add(0);
        payload.Add(1); // one global name against zero globals
        payload.Add(0);

        AssertRejected(Append(stripped, payload.ToArray()), "global name(s) for");
    }

    // ------------------------------------------------------------------ helpers

    private static int IndexOf(BytecodeModule module, string name)
    {
        for (var i = 0; i < module.Functions.Count; i++)
            if (module.Functions[i].Name == name) return i;
        Assert.Fail($"no function named '{name}'");
        return -1;
    }

    /// <summary>The program's bytes WITHOUT section 13, so a handcrafted one can be appended
    /// while the ids stay ascending.</summary>
    private static byte[] StrippedProgram() =>
        BytecodeWriter.Write(Lower(Program), debugInfo: false);

    private static byte[] Append(byte[] module, byte[] payload)
    {
        var result = new List<byte>(module) { (byte)SectionId.DebugInfo };
        AppendULeb(result, (ulong)payload.Length);
        result.AddRange(payload);
        return result.ToArray();
    }

    private static void AppendULeb(List<byte> bytes, ulong value)
    {
        do
        {
            var group = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value == 0 ? group : (byte)(group | 0x80));
        } while (value != 0);
    }

    private static void AssertRejected(byte[] bytes, string messagePart)
    {
        var ex = Assert.Throws<MalformedBytecodeException>(() => BytecodeReader.ReadOrThrow(bytes));
        Assert.Contains(messagePart, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The section ids in file order, straight off the byte stream — the same
    /// reader-independent walk <c>BytecodeTests.SectionIds</c> does.</summary>
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
}
