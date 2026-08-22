using System.Runtime.CompilerServices;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// An attribute argument may NAME its value (2.4), and the host reads the value all the same.
///
/// <para>The fault class this closes sits on the receiving side of an event. A game publishes its
/// vocabulary as a module a mod imports; before this, the mod still had to repeat the raw string
/// in the attribute, so a typo produced a handler nobody ever calls — silent, and found much
/// later. With the name checked at compile time, the typo is <c>unknown identifier</c>.</para>
///
/// <para>The row is what these tests read: the sema deciding the use is legal is only half of it,
/// and a value that never reaches the module would be a compiler that agreed and then forgot.
/// </para>
/// </summary>
public sealed class AttributeConstantTests : IDisposable
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-attr-" + Guid.NewGuid().ToString("N")[..8]);

    public AttributeConstantTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void Module(string modulePath, string source)
    {
        var file = Path.Combine(_dir, Path.Combine(modulePath.Split('.')) + ".lyr");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, source);
    }

    private LangVm Vm() => new(new HostOptions
    {
        StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
        SourceRoot = _dir,
        Capabilities = Capability.None,
    });

    [Fact]
    public void A_named_value_reaches_the_row()
    {
        var vm = Vm();
        var module = vm.Compile("""
            import std.core { OnFunction };

            pub struct On :: [OnFunction] { event: string, priority: int }

            let CLEARED = "tetris.cleared";
            let LATE = 10;

            @On { event = CLEARED, priority = LATE }
            pub fn onCleared(): void { }
            """, "mod");

        var row = Assert.Single(module.Attributes.OnFunctions("On"));
        Assert.Equal("tetris.cleared", row.Value("event")?.Text);
        Assert.Equal(10, row.Value("priority")?.AsInt);
    }

    [Fact]
    public void A_value_named_across_a_module_boundary_reaches_the_row()
    {
        // The shape the requirement was filed for: the game publishes its event names, the mod
        // imports them, and the compiler checks what used to be a bare string.
        Module("tetris_api", """
            module tetris_api;

            pub let CLEARED = "tetris.cleared";
            """);

        var vm = Vm();
        var selective = vm.Compile("""
            import std.core { OnFunction };
            import tetris_api { CLEARED };

            pub struct On :: [OnFunction] { event: string }

            @On { event = CLEARED }
            pub fn onCleared(): void { }
            """, "mod");

        var qualified = vm.Compile("""
            import std.core { OnFunction };
            import tetris_api;

            pub struct On :: [OnFunction] { event: string }

            @On { event = tetris_api.CLEARED }
            pub fn onCleared(): void { }
            """, "other");

        Assert.Equal("tetris.cleared",
            Assert.Single(selective.Attributes.OnFunctions("On")).Value("event")?.Text);
        Assert.Equal("tetris.cleared",
            Assert.Single(qualified.Attributes.OnFunctions("On")).Value("event")?.Text);
    }

    [Fact]
    public void A_named_default_reaches_the_row()
    {
        var vm = Vm();
        var module = vm.Compile("""
            import std.core { OnFunction };

            let DEFAULT_LIMIT = 3;

            pub struct Retry :: [OnFunction] { limit: int = DEFAULT_LIMIT }

            @Retry
            pub fn fetch(): void { }
            """, "mod");

        Assert.Equal(3, Assert.Single(module.Attributes.OnFunctions("Retry")).Value("limit")?.AsInt);
    }

    [Fact]
    public void A_negative_named_value_keeps_its_sign()
    {
        var vm = Vm();
        var module = vm.Compile("""
            import std.core { OnFunction };

            let FOREVER = -1;

            pub struct Retry :: [OnFunction] { limit: int }

            @Retry { limit = FOREVER }
            pub fn fetch(): void { }
            """, "mod");

        Assert.Equal(-1, Assert.Single(module.Attributes.OnFunctions("Retry")).Value("limit")?.AsInt);
    }

    [Fact]
    public void The_type_row_of_an_attributed_type_takes_names_too()
    {
        var vm = Vm();
        var module = vm.Compile("""
            import std.core { OnType };

            let PHYSICS = 2;

            pub struct Component :: [OnType] { stage: int }

            @Component { stage = PHYSICS }
            pub struct Body { x: float, y: float }
            """, "mod");

        var row = Assert.Single(module.Attributes.OnTypes("Component"));
        Assert.Equal(2, row.Value("stage")?.AsInt);
        var fields = module.Attributes.FieldsOf(row.Target);
        Assert.NotNull(fields);
        Assert.Equal(["x", "y"], fields.Select(f => f.Name));
    }

    [Fact]
    public void An_enum_value_reaches_the_row_as_a_name_and_a_tag()
    {
        // Both halves, because a host wants the name and a program comparing rows wants the
        // number: Text is the qualified variant, Bits the tag slot 0 carries at runtime.
        var vm = Vm();
        var module = vm.Compile("""
            import std.core { OnFunction };

            pub enum Layout { Packed, Separate }

            pub struct Saved :: [OnFunction] { layout: Layout }

            @Saved { layout = Layout.Separate }
            pub fn store(): void { }
            """, "mod");

        var row = Assert.Single(module.Attributes.OnFunctions("Saved"));
        Assert.Equal("Layout.Separate", row.Value("layout")?.Text);
        Assert.Equal(1, row.Value("layout")?.AsInt);
    }

    [Fact]
    public void The_first_variant_is_tag_zero()
    {
        var vm = Vm();
        var module = vm.Compile("""
            import std.core { OnFunction };

            pub enum Layout { Packed, Separate }

            pub struct Saved :: [OnFunction] { layout: Layout = Layout.Packed }

            @Saved
            pub fn store(): void { }
            """, "mod");

        var row = Assert.Single(module.Attributes.OnFunctions("Saved"));
        Assert.Equal("Layout.Packed", row.Value("layout")?.Text);
        Assert.Equal(0, row.Value("layout")?.AsInt);
    }

    [Fact]
    public void An_enum_value_survives_a_round_trip_through_bytes()
    {
        // The row is read back from the BYTES a second time here: the encoding carries the tag
        // alone, and the name is resolved from the field's type — so a reader that never saw the
        // source still answers 'Layout.Separate'.
        var vm = Vm();
        var compiled = vm.Compile("""
            import std.core { OnFunction };

            pub enum Layout { Packed, Separate }

            pub struct Saved :: [OnFunction] { layout: Layout }

            @Saved { layout = Layout.Separate }
            pub fn store(): void { }
            """, "mod");

        var reread = ModuleAttributes.Of(BytecodeReader.ReadOrThrow(compiled.Bytes));
        var row = Assert.Single(reread.OnFunctions("Saved"));

        Assert.Equal("Layout.Separate", row.Value("layout")?.Text);
        Assert.Equal(1, row.Value("layout")?.AsInt);
    }

    /// <summary>
    /// A default written in the module that DECLARES the attribute, read in a module that uses
    /// it. The default's meaning is settled when its own declaration is checked, so checking a
    /// use first asked a question nobody had answered yet: '@Saved' without arguments failed
    /// across a module line while the same code in one file compiled.
    /// </summary>
    [Fact]
    public void An_attribute_default_holds_across_the_module_that_uses_it()
    {
        Module("engine.save", """
            module engine.save;

            import std.core { OnType };

            pub enum Layout { Shared, Separate }

            pub struct Saved :: [OnType] { layout: Layout = Layout.Shared, version: int = 1 }
            """);

        var vm = Vm();
        var module = vm.Compile("""
            import engine.save { Saved };

            @Saved
            pub class Holder { n: int = 0 }
            """, "mod");

        var row = Assert.Single(module.Attributes.OnTypes("Saved"));
        Assert.Equal("Layout.Shared", row.Value("layout")?.Text);
        Assert.Equal(1, row.Value("version")?.AsInt);
    }

    [Fact]
    public void A_use_that_writes_one_field_still_takes_the_others_from_their_defaults()
    {
        // The shape that made the finding sharp: '@Saved { version = 2 }' failed for the field it
        // did NOT write.
        Module("engine.save", """
            module engine.save;

            import std.core { OnType };

            pub enum Layout { Shared, Separate }

            pub struct Saved :: [OnType] { layout: Layout = Layout.Shared, version: int = 1 }
            """);

        var vm = Vm();
        var module = vm.Compile("""
            import engine.save { Saved };

            @Saved { version = 2 }
            pub class Holder { n: int = 0 }
            """, "mod");

        var row = Assert.Single(module.Attributes.OnTypes("Saved"));
        Assert.Equal("Layout.Shared", row.Value("layout")?.Text);
        Assert.Equal(2, row.Value("version")?.AsInt);
    }

    [Fact]
    public void A_named_constant_default_crosses_the_module_line_too()
    {
        // The same question for the 2.4 form: a default that is a 'let' rather than a variant.
        Module("engine.retry", """
            module engine.retry;

            import std.core { OnFunction };

            pub let DEFAULT_LIMIT = 3;

            pub struct Retry :: [OnFunction] { limit: int = DEFAULT_LIMIT }
            """);

        var vm = Vm();
        var module = vm.Compile("""
            import engine.retry { Retry };

            @Retry
            pub fn fetch(): void { }
            """, "mod");

        Assert.Equal(3, Assert.Single(module.Attributes.OnFunctions("Retry")).Value("limit")?.AsInt);
    }
}
