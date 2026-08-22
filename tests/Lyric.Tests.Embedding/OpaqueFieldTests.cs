using System.Runtime.CompilerServices;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// What a host reading an attributed class learns about a field that is a HANDLE.
///
/// <para>The finding these are written from: a save writer walks the fields of a <c>@Saved</c>
/// class and writes each by its type. A handle must not be written — an entity handle carries a
/// slot and a generation, and after a restart the slot belongs to someone else, so a restored
/// handle names the wrong thing. The writer WANTS to refuse it and could not see it: an
/// <c>opaque type Entity = int</c> is an <c>i64</c> below the checker, exactly like a level
/// number.</para>
///
/// <para>The shape below is the one that produced it — the engine declares the handle, an SDK
/// module declares the attribute, and the game writes the class in a third. Nothing about the
/// program changes; the host simply has a name where it had nothing.</para>
/// </summary>
public sealed class OpaqueFieldTests : IDisposable
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-opaque-" + Guid.NewGuid().ToString("N")[..8]);

    public OpaqueFieldTests()
    {
        Directory.CreateDirectory(_dir);

        Module("world", """
            module world;

            pub opaque type Entity = int;

            pub fn spawn(): Entity { return 1 as Entity; }
            """);

        Module("engine.save", """
            module engine.save;

            import std.core { OnType };

            pub struct Saved :: [OnType] { version: int = 1 }
            """);
    }

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

    private const string Holder = """
        import world;
        import engine.save { Saved };

        @Saved
        pub class Holder {
            hero: world.Entity = 0 as world.Entity,
            stage: int = 0
        }
        """;

    [Fact]
    public void A_save_writer_can_tell_a_handle_from_the_number_it_is_made_of()
    {
        var module = Vm().Compile(Holder, "game");

        var row = Assert.Single(module.Attributes.OnTypes("Saved"));
        var fields = module.Attributes.FieldsOf(row.Target);
        Assert.NotNull(fields);

        Assert.Equal(["hero", "stage"], fields.Select(f => f.Name));

        // Both are i64 and stay so — the type is the truth about the value, and the name is the
        // truth about what the source meant by it.
        Assert.All(fields, f => Assert.Equal(TypeTag.I64, f.Type.Tag));
        Assert.Equal("Entity", fields[0].OpaqueName);
        Assert.Null(fields[1].OpaqueName);
    }

    [Fact]
    public void The_name_survives_the_round_trip_through_bytes()
    {
        // The question a host actually asks is asked of FOREIGN bytes: a mod it did not compile
        // and only just read. Nothing here comes from the compilation any more.
        var compiled = Vm().Compile(Holder, "game");
        var reread = ModuleAttributes.Of(BytecodeReader.ReadOrThrow(compiled.Bytes));

        var fields = reread.FieldsOf(Assert.Single(reread.OnTypes("Saved")).Target);
        Assert.NotNull(fields);
        Assert.Equal("Entity", fields[0].OpaqueName);
    }

    [Fact]
    public void A_class_of_ordinary_fields_answers_null_for_every_one_of_them()
    {
        var module = Vm().Compile("""
            import engine.save { Saved };

            @Saved
            pub class Holder { stage: int = 0, name: string = "" }
            """, "game");

        var fields = module.Attributes.FieldsOf(Assert.Single(module.Attributes.OnTypes("Saved")).Target);
        Assert.NotNull(fields);
        Assert.All(fields, f => Assert.Null(f.OpaqueName));
    }

    [Fact]
    public void The_program_runs_exactly_as_it_did()
    {
        // The section describes and does nothing, and the point of saying so in a test is that a
        // reader which ignores section 14 — every runtime before 3.5 — loses nothing else. The
        // handle is used the way a handle is used: made, kept in a field, handed back as its
        // underlying value.
        var vm = Vm();
        var instance = vm.Instantiate(vm.Compile(Holder + """

            pub fn heroOf(): int {
                let h = Holder { hero = world.spawn(), stage = 7 };
                return h.hero as int;
            }
            """, "game"));

        Assert.Equal(1, instance.Call<int>("heroOf"));
    }
}
