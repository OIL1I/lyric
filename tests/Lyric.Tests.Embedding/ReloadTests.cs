using System.Runtime.CompilerServices;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// Hot reload.
///
/// <para>THE LOAD-BEARING PROMISE IS NOT "it reloads" BUT "the old version survives a failure". A mod
/// someone saves with a typo must not stop the game — the same property the REPL has: a faulty input
/// changes nothing. Without this test <c>Reload</c> would only be an alias for
/// <c>Instantiate(CompileFile(...))</c>, which the host could write itself.</para>
///
/// <para>What runs anew and what stays fell out of two earlier decisions by itself: the module constants
/// are recomputed, because a new instance is a new state, and host objects survive, because they belong
/// to the GC rather than to the instance.</para>
/// </summary>
public class ReloadTests : IDisposable
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-reload-" + Guid.NewGuid().ToString("N")[..8]);

    public ReloadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private LangVm Vm() => new(new HostOptions
    {
        StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
    });

    private string Write(string source)
    {
        var path = Path.Combine(_dir, "mod.lyr");
        File.WriteAllText(path, source);
        return path;
    }

    [Fact]
    public void A_reload_picks_up_the_new_source()
    {
        var vm = Vm();
        var path = Write("pub fn wert(): int { return 1; }");
        var instance = vm.Instantiate(vm.CompileFile(path));

        Assert.Equal(1, instance.Call<long>("wert"));

        File.WriteAllText(path, "pub fn wert(): int { return 2; }");
        instance = instance.Reload();

        Assert.Equal(2, instance.Call<long>("wert"));
    }

    /// <summary>
    /// The actual test. When the new version fails, <c>Reload</c> throws and the old instance runs on as
    /// if nothing had happened.
    /// </summary>
    [Fact]
    public void A_failed_reload_leaves_the_old_instance_working()
    {
        var vm = Vm();
        var path = Write("pub fn wert(): int { return 1; }");
        var instance = vm.Instantiate(vm.CompileFile(path));

        File.WriteAllText(path, "pub fn wert(): int { return unbekannt; }");

        var thrown = Assert.Throws<EmbeddingException>(() => instance.Reload());
        Assert.Contains(thrown.Diagnostics, d => d.Code == "LYR-SEM0002");

        // Usable unchanged, and repeatedly rather than only once.
        Assert.Equal(1, instance.Call<long>("wert"));
        Assert.Equal(1, instance.Call<long>("wert"));
    }

    /// <summary>
    /// The module constants are recomputed: a new instance is a new state. Without this test it would stay
    /// open whether <c>Reload</c> passes the old state on, and that would be the fault that shows only
    /// when a mod author wonders why their change does nothing.
    /// </summary>
    [Fact]
    public void A_reload_re_runs_the_module_constants()
    {
        var vm = Vm();
        var path = Write("""
            class Zaehler { stand: int = 0 }
            let z = Zaehler { };
            pub fn hoch(): int { z.stand = z.stand + 1; return z.stand; }
            """);

        var instance = vm.Instantiate(vm.CompileFile(path));

        Assert.Equal(1, instance.Call<long>("hoch"));
        Assert.Equal(2, instance.Call<long>("hoch"));

        instance = instance.Reload();

        // At 1 rather than at 3: the initializer ran anew.
        Assert.Equal(1, instance.Call<long>("hoch"));
    }

    /// <summary>
    /// A host object survives the reload: it belongs to the GC rather than to the instance. That is the
    /// property making hot reload usable in a game — the world stays, only the script is exchanged.
    /// </summary>
    [Fact]
    public void A_host_object_survives_a_reload()
    {
        var welt = new HostTypeTests.Entity("bleibt") { Hp = 50 };

        var vm = Vm();
        vm.RegisterType<HostTypeTests.Entity>("Entity", t => t
            .Getter("leben", (HostTypeTests.Entity e) => e.Hp)
            .Method("schaden", (HostTypeTests.Entity e, long n) => { e.Hp -= n; }, mutates: true));
        vm.RegisterFunction("held", () => welt);

        var path = Write("""
            import host { held };
            pub fn treffer(): int { let e = held(); e.schaden(1); return e.leben(); }
            """);

        var instance = vm.Instantiate(vm.CompileFile(path));

        Assert.Equal(49, instance.Call<long>("treffer"));
        Assert.Equal(48, instance.Call<long>("treffer"));

        File.WriteAllText(path, """
            import host { held };
            pub fn treffer(): int { let e = held(); e.schaden(10); return e.leben(); }
            """);

        instance = instance.Reload();

        // The world remembered what happened before; only the rule changed.
        Assert.Equal(38, instance.Call<long>("treffer"));
        Assert.Equal(38, welt.Hp);
    }

    [Fact]
    public void An_in_memory_module_cannot_be_reloaded()
    {
        var vm = Vm();
        var instance = vm.Instantiate(vm.Compile("pub fn wert(): int { return 1; }", "mod"));

        Assert.Null(instance.Module.Origin);

        var thrown = Assert.Throws<ScriptException>(() => instance.Reload());
        Assert.Equal("LYR-EMB0008", thrown.Code);
    }

    /// <summary>The old instance stays usable after a SUCCESSFUL reload as well; it is only no longer the
    /// current one. A host still holding a reference therefore does not fall into a half-cleared
    /// state.</summary>
    [Fact]
    public void The_old_instance_still_works_after_a_successful_reload()
    {
        var vm = Vm();
        var path = Write("pub fn wert(): int { return 1; }");
        var alt = vm.Instantiate(vm.CompileFile(path));

        File.WriteAllText(path, "pub fn wert(): int { return 2; }");
        var neu = alt.Reload();

        Assert.Equal(2, neu.Call<long>("wert"));
        Assert.Equal(1, alt.Call<long>("wert"));
    }
}
