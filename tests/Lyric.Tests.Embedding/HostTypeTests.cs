using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// Host-Typen (M10/E4a, ADR-026): ein .NET-Objekt reist durch ein Skript, ohne dass das Skript
/// hineinsehen kann.
///
/// <para><b>Die tragende Zusage ist Identitaet, nicht Gleichheit.</b> Was der Host
/// zurueckbekommt, ist <c>ReferenceEquals</c> dasselbe Objekt — es wird nicht kopiert, nicht
/// gewrappt, nicht neu erzeugt. Genau das macht ADR-026 aus: die Referenz reist, der GC haelt
/// sie, und niemand dazwischen fasst sie an.</para>
///
/// <para><b>Und die Gegenrichtung wird ebenso geprueft</b>: ein Skript kann keinen Host-Typ
/// konstruieren und hat keine Felder daran. Ohne diese beiden Tests waere „ein Host-Typ hat kein
/// Layout, das das Modul kennt" eine Behauptung — und sie ist die Zusage, an der E4 wirklich
/// falsch werden koennte.</para>
/// </summary>
public class HostTypeTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>Ein Host-Objekt mit Zustand — damit sichtbar wird, dass es dasselbe bleibt.</summary>
    public sealed class Entity
    {
        public Entity(string name) => Name = name;
        public string Name { get; }
        public long Hp { get; set; } = 100;
    }

    /// <summary>Ein zweiter Typ. Mit nur einem bliebe jeder Test gruen, wenn alle Host-Typen
    /// dasselbe waeren — dieselbe Lehre wie beim Dispatch-Test aus M8/S4.</summary>
    public sealed class Sprite
    {
        public Sprite(string file) => File = file;
        public string File { get; }
    }

    private static LangVm Vm() => new(new HostOptions
    {
        StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
    });

    // ------------------------------------------------------------------ hin und zurueck

    [Fact]
    public void A_host_object_travels_through_a_script_and_comes_back_identical()
    {
        var welt = new Entity("goblin");
        Entity? zurueck = null;

        var vm = Vm();
        vm.RegisterType<Entity>("Entity");
        vm.RegisterFunction("spawn", (string _) => welt);
        vm.RegisterFunction("behalten", (Entity e) => { zurueck = e; });

        var instance = vm.Instantiate(vm.Compile("""
            import host { spawn, behalten };
            pub fn los() { behalten(spawn("goblin")); }
            """, "mod"));

        instance.CallVoid("los");

        // ReferenceEquals und nicht Equals: die Zusage ist, dass NICHTS dazwischen es angefasst
        // hat. Ein Kopieren oder Wrappen waere mit Wert-Gleichheit nicht zu bemerken.
        Assert.True(ReferenceEquals(welt, zurueck));
    }

    [Fact]
    public void The_host_sees_a_mutation_it_made_through_its_own_function()
    {
        var welt = new Entity("ork") { Hp = 100 };

        var vm = Vm();
        vm.RegisterType<Entity>("Entity");
        vm.RegisterFunction("hole", () => welt);
        vm.RegisterFunction("schaden", (Entity e, long wieviel) => { e.Hp -= wieviel; });
        vm.RegisterFunction("leben", (Entity e) => e.Hp);

        var instance = vm.Instantiate(vm.Compile("""
            import host { hole, schaden, leben };
            pub fn los(): int {
                let e = hole();
                schaden(e, 30);
                schaden(e, 5);
                return leben(e);
            }
            """, "mod"));

        Assert.Equal(65, instance.Call<long>("los"));
        Assert.Equal(65, welt.Hp);
    }

    /// <summary>
    /// Zwei Host-Typen duerfen nicht verwechselt werden. Der Name ist alles, was sie unterscheidet
    /// — ohne die Pruefung beim Binden bekaeme ein Modul, das eine <c>Entity</c> erwartet, eine
    /// <c>Sprite</c>, und der erste Zugriff waere ein Cast-Fehler tief im Host.
    /// </summary>
    [Fact]
    public void Two_host_types_are_not_interchangeable()
    {
        var vm = Vm();
        vm.RegisterType<Entity>("Entity");
        vm.RegisterType<Sprite>("Sprite");
        vm.RegisterFunction("entity", () => new Entity("e"));
        vm.RegisterFunction("nameOf", (Sprite s) => s.File);

        var thrown = Assert.Throws<EmbeddingException>(() => vm.Compile("""
            import host { entity, nameOf };
            pub fn los(): string { return nameOf(entity()); }
            """, "mod"));

        Assert.Contains(thrown.Diagnostics, d => d.Code == "LYR-SEM0001");
    }

    [Fact]
    public void A_host_object_can_be_stored_in_a_module_constant_and_survives_calls()
    {
        var welt = new Entity("bleibt");

        var vm = Vm();
        vm.RegisterType<Entity>("Entity");
        vm.RegisterFunction("hole", () => welt);
        vm.RegisterFunction("schaden", (Entity e, long n) => { e.Hp -= n; });
        vm.RegisterFunction("leben", (Entity e) => e.Hp);

        var instance = vm.Instantiate(vm.Compile("""
            import host { hole, schaden, leben };

            let gemerkt = hole();

            pub fn tick(): int {
                schaden(gemerkt, 1);
                return leben(gemerkt);
            }
            """, "mod"));

        // Der Zustand ueberlebt die Aufrufe — das ist die beobachtbare Seite von ADR-026, und
        // sie ist testbar, waehrend "der GC haelt es am Leben" es nicht waere: ein Test mit
        // GC.Collect() und WeakReference pruefte die Laune des GC, nicht unsere Zusage.
        Assert.Equal(99, instance.Call<long>("tick"));
        Assert.Equal(98, instance.Call<long>("tick"));
        Assert.Equal(97, instance.Call<long>("tick"));
        Assert.Equal(97, welt.Hp);
    }

    // ------------------------------------------------------------------ was das Skript NICHT darf

    /// <summary>
    /// Ein Skript kann keinen Host-Typ erzeugen. <b>Ohne diese Diagnose war es ein
    /// Compiler-Absturz</b> (`cannot compare IrHostType with IrRefType`, gemessen am 2026-08-11):
    /// das Lowering legte ein Objekt nach der leeren Klassendeklaration an, waehrend die Variable
    /// den Host-Typ trug.
    /// </summary>
    [Fact]
    public void A_script_cannot_construct_a_host_type()
    {
        var vm = Vm();
        vm.RegisterType<Entity>("Entity");
        vm.RegisterFunction("leben", (Entity e) => e.Hp);

        var thrown = Assert.Throws<EmbeddingException>(() => vm.Compile("""
            import host;
            pub fn los(): int { return host.leben(host.Entity { }); }
            """, "mod"));

        var diagnostic = Assert.Single(thrown.Diagnostics, d => d.Code == "LYR-SEM0061");
        Assert.Contains("only the host can create one", diagnostic.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Und es hat keine Felder daran. Das ist die Zusage, die ADR-026 <b>strukturell</b> macht —
    /// ein Host-Typ hat keinen Typtabellen-Eintrag, also gibt es kein Feld, das ein
    /// <c>ldfld</c> nennen koennte.
    /// </summary>
    [Fact]
    public void A_script_has_no_fields_on_a_host_type()
    {
        var vm = Vm();
        vm.RegisterType<Entity>("Entity");
        vm.RegisterFunction("hole", () => new Entity("x"));

        var thrown = Assert.Throws<EmbeddingException>(() => vm.Compile("""
            import host { hole };
            pub fn los(): int { return hole().Hp; }
            """, "mod"));

        Assert.NotEmpty(thrown.Diagnostics);
    }

    // ------------------------------------------------------------------ die erzeugte Deklaration

    [Fact]
    public void The_generated_module_declares_the_host_type_as_an_empty_class()
    {
        var vm = Vm();
        vm.RegisterType<Entity>("Entity");
        vm.RegisterFunction("hole", () => new Entity("x"));

        var source = vm.HostModuleSource;

        Assert.NotNull(source);
        Assert.Contains("pub class Entity { }", source, StringComparison.Ordinal);
        Assert.Contains("pub fn hole(): Entity;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Registering_the_same_dotnet_type_twice_is_an_error()
    {
        var vm = Vm();
        vm.RegisterType<Entity>("Entity");

        Assert.Throws<ArgumentException>(() => vm.RegisterType<Entity>("Andere"));
        Assert.Throws<ArgumentException>(() => vm.RegisterType<Sprite>("Entity"));
    }

    // ------------------------------------------------------------------ Methoden (E4b)

    /// <summary>
    /// Das E4b-Gate: <c>e.damage(5)</c> statt <c>damage(e, 5)</c>. Der Empfaenger ist Parameter 0
    /// (ADR-014) — dieselbe Konvention wie bei jeder anderen Methode, nur dass die
    /// Implementierung beim Host liegt.
    /// </summary>
    [Fact]
    public void A_script_calls_methods_on_a_host_object()
    {
        var welt = new Entity("ork") { Hp = 100 };

        var vm = Vm();
        vm.RegisterType<Entity>("Entity", t => t
            .Getter("leben", (Entity e) => e.Hp)
            .Getter("name", (Entity e) => e.Name)
            .Method("schaden", (Entity e, long wieviel) => { e.Hp -= wieviel; }, mutates: true));
        vm.RegisterFunction("hole", () => welt);

        var instance = vm.Instantiate(vm.Compile("""
            import host { hole };

            pub fn los(): int {
                let e = hole();
                e.schaden(30);
                e.schaden(5);
                return e.leben();
            }

            pub fn wer(): string { return hole().name(); }
            """, "mod"));

        Assert.Equal(65, instance.Call<long>("los"));
        Assert.Equal(65, welt.Hp);
        Assert.Equal("ork", instance.Call<string>("wer"));
    }

    /// <summary>
    /// Zwei Host-Typen mit <b>gleichnamigen</b> Methoden. Ohne den Typ im gemangelten Namen
    /// bekaeme der eine die Implementierung des anderen — und mit nur einem Typ waere der Test
    /// dafuer blind. Dieselbe Lehre wie beim Dispatch-Test aus M8/S4.
    /// </summary>
    [Fact]
    public void Two_host_types_may_have_methods_of_the_same_name()
    {
        var vm = Vm();
        vm.RegisterType<Entity>("Entity", t => t.Getter("bezeichnung", (Entity e) => e.Name));
        vm.RegisterType<Sprite>("Sprite", t => t.Getter("bezeichnung", (Sprite s) => s.File));
        vm.RegisterFunction("entity", () => new Entity("ork"));
        vm.RegisterFunction("sprite", () => new Sprite("ork.png"));

        var instance = vm.Instantiate(vm.Compile("""
            import host { entity, sprite };
            pub fn a(): string { return entity().bezeichnung(); }
            pub fn b(): string { return sprite().bezeichnung(); }
            """, "mod"));

        Assert.Equal("ork", instance.Call<string>("a"));
        Assert.Equal("ork.png", instance.Call<string>("b"));
    }

    [Fact]
    public void The_generated_class_carries_its_methods()
    {
        var vm = Vm();
        vm.RegisterType<Entity>("Entity", t => t
            .Getter("leben", (Entity e) => e.Hp)
            .Method("schaden", (Entity e, long n) => { e.Hp -= n; }, mutates: true));

        var source = vm.HostModuleSource;

        Assert.NotNull(source);
        Assert.Contains("pub fn leben(): int;,", source, StringComparison.Ordinal);
        Assert.Contains("pub mut fn schaden(n: int): void;,", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Der erste Parameter MUSS der Empfaenger sein — sonst haette der Host eine Methode auf
    /// <c>Entity</c> geschrieben, die ein <c>Sprite</c> erwartet, und das fiele erst beim Binden
    /// auf.
    ///
    /// <para>Ueber <c>Getter&lt;TValue&gt;(Func&lt;T, TValue&gt;)</c> ist der Fall gar nicht
    /// schreibbar — dort erzwingt C#s Typsystem den Empfaenger, und das ist die bessere Stelle
    /// dafuer. Die Pruefung zur Laufzeit deckt <c>Method(string, Delegate)</c> ab, das
    /// untypisiert ist, weil eine Methode beliebig viele Parameter haben darf.</para>
    /// </summary>
    [Fact]
    public void A_method_whose_first_parameter_is_not_the_receiver_is_refused()
    {
        var vm = Vm();
        vm.RegisterType<Sprite>("Sprite");

        var thrown = Assert.Throws<ArgumentException>(() => vm.RegisterType<Entity>("Entity",
            t => t.Method("falsch", (Sprite s) => s.File)));

        Assert.Contains("must be the receiver", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicate_method_name_on_one_type_is_an_error()
    {
        var vm = Vm();

        Assert.Throws<ArgumentException>(() => vm.RegisterType<Entity>("Entity", t => t
            .Getter("leben", (Entity e) => e.Hp)
            .Getter("leben", (Entity e) => e.Hp)));
    }

    /// <summary>Ein nicht registrierter .NET-Typ bleibt draussen — mit einer Meldung, die den
    /// Ausweg nennt.</summary>
    [Fact]
    public void An_unregistered_type_is_still_refused()
    {
        var vm = Vm();

        var thrown = Assert.Throws<ArgumentException>(
            () => vm.RegisterFunction("hole", () => new Entity("x")));

        Assert.Contains("RegisterType", thrown.Message, StringComparison.Ordinal);
    }
}
