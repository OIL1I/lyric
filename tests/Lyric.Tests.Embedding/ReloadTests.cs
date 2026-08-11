using System.Runtime.CompilerServices;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// Hot-Reload (M10/E5).
///
/// <para><b>Die tragende Zusage ist nicht „es laedt neu", sondern „die alte Fassung ueberlebt
/// einen Fehlschlag".</b> Ein Mod, den jemand mit einem Tippfehler speichert, darf das Spiel
/// nicht anhalten — dieselbe Eigenschaft, die die REPL seit ADR-021 hat: eine fehlerhafte Eingabe
/// aendert nichts. Ohne diesen Test waere <c>Reload</c> nur ein Alias fuer
/// <c>Instantiate(CompileFile(...))</c>, das der Host auch selbst schreiben koennte.</para>
///
/// <para>Was neu laeuft und was bleibt, fiel aus zwei fruehreren Entscheidungen von selbst heraus:
/// die Modul-Konstanten werden neu berechnet, weil eine neue Instanz ein neuer Zustand ist
/// (ADR-025), und Host-Objekte ueberleben, weil sie dem GC gehoeren und nicht der Instanz
/// (ADR-026).</para>
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
    /// <b>Der eigentliche Test.</b> Scheitert die neue Fassung, wirft <c>Reload</c> — und die alte
    /// Instanz laeuft weiter, als waere nichts gewesen.
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

        // Unveraendert benutzbar — und zwar mehrfach, nicht nur einmal.
        Assert.Equal(1, instance.Call<long>("wert"));
        Assert.Equal(1, instance.Call<long>("wert"));
    }

    /// <summary>
    /// Die Modul-Konstanten werden neu berechnet: eine neue Instanz ist ein neuer Zustand
    /// (ADR-025). Ohne diesen Test bliebe offen, ob <c>Reload</c> den alten Zustand weiterreicht —
    /// und genau das waere der Fehler, der erst auffaellt, wenn ein Mod-Autor sich wundert, warum
    /// seine Aenderung nichts tut.
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

        // Bei 1 und nicht bei 3: der Initialisierer lief neu.
        Assert.Equal(1, instance.Call<long>("hoch"));
    }

    /// <summary>
    /// Ein Host-Objekt ueberlebt den Reload — es gehoert dem GC und nicht der Instanz (ADR-026).
    /// Das ist die Eigenschaft, die Hot-Reload in einem Spiel brauchbar macht: die Welt bleibt
    /// stehen, nur das Skript wird ausgetauscht.
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

        // Die Welt hat sich gemerkt, was vorher passiert ist — nur die Regel hat sich geaendert.
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

    /// <summary>Die alte Instanz bleibt nach einem ERFOLGREICHEN Reload ebenfalls benutzbar — sie
    /// ist nur nicht mehr die aktuelle. Ein Host, der noch eine Referenz haelt, faellt damit nicht
    /// in einen halb abgeraeumten Zustand.</summary>
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
