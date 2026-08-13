using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Embedding;

namespace Lyric.Tests.Embedding;

/// <summary>
/// Host types: a .NET object travels through a script without the script being able to look inside.
///
/// <para>The load-bearing promise is IDENTITY rather than equality. What the host gets back is
/// <c>ReferenceEquals</c> the same object — it is not copied, not wrapped, not recreated. The
/// reference travels, the GC holds it, and nobody in between touches it.</para>
///
/// <para>The other direction is checked as well: a script cannot construct a host type and has no
/// fields on it. Without those two tests "a host type has no layout the module knows" would be a
/// claim.</para>
/// </summary>
public class HostTypeTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>A host object with state, so it becomes visible that it stays the same.</summary>
    public sealed class Entity
    {
        public Entity(string name) => Name = name;
        public string Name { get; }
        public long Hp { get; set; } = 100;
    }

    /// <summary>A second type. With only one, every test would stay green even if all host types were the
    /// same.</summary>
    public sealed class Sprite
    {
        public Sprite(string file) => File = file;
        public string File { get; }
    }

    private static LangVm Vm() => new(new HostOptions
    {
        StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
    });

    // ------------------------------------------------------------------ there and back

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

        // ReferenceEquals rather than Equals: the promise is that NOTHING in between touched it. A copy
        // or a wrap would not be noticeable with value equality.
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
    /// Two host types must not be confused. The name is all that distinguishes them; without the check
    /// while binding, a module expecting an <c>Entity</c> would get a <c>Sprite</c>, and the first access
    /// would be a cast failure deep in the host.
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

        // The state survives the calls: that is the observable side, and it is testable, while "the GC
        // keeps it alive" would not be — a test with GC.Collect() and a WeakReference would check the
        // mood of the GC rather than the promise.
        Assert.Equal(99, instance.Call<long>("tick"));
        Assert.Equal(98, instance.Call<long>("tick"));
        Assert.Equal(97, instance.Call<long>("tick"));
        Assert.Equal(97, welt.Hp);
    }

    // ------------------------------------------------------------------ what the script may NOT do

    /// <summary>
    /// A script cannot create a host type. Without this diagnostic it is a compiler crash
    /// (`cannot compare IrHostType with IrRefType`): the lowering allocates an object after the empty
    /// class declaration while the variable carries the host type.
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
    /// And it has no fields on it. That promise is STRUCTURAL:
    /// a host type has no type table entry, so there is no field an <c>ldfld</c> could name.
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

    // ------------------------------------------------------------------ the generated declaration

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
    /// <c>e.damage(5)</c> rather than <c>damage(e, 5)</c>. The receiver is parameter 0, the same
    /// convention as for every other method, except that the implementation lives at the host.
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
    /// Two host types with methods OF THE SAME NAME. Without the type in the mangled name one would get
    /// the other's implementation, and with only one type the test would be blind to that.
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
    /// The first parameter MUST be the receiver; otherwise the host would have written a method on
    /// <c>Entity</c> that expects a <c>Sprite</c>, and that would show only while binding.
    ///
    /// <para>Through <c>Getter&lt;TValue&gt;(Func&lt;T, TValue&gt;)</c> the case cannot even be written:
    /// there C#'s type system enforces the receiver, and that is the better place for it. The runtime
    /// check covers <c>Method(string, Delegate)</c>, which is untyped, because a method may have any
    /// number of parameters.</para>
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

    /// <summary>An unregistered .NET type stays outside, with a message that names the way out.</summary>
    [Fact]
    public void An_unregistered_type_is_still_refused()
    {
        var vm = Vm();

        var thrown = Assert.Throws<ArgumentException>(
            () => vm.RegisterFunction("hole", () => new Entity("x")));

        Assert.Contains("RegisterType", thrown.Message, StringComparison.Ordinal);
    }
}
