using System.Runtime.CompilerServices;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// `Map&lt;K, V&gt;` — open addressing with linear probing, written in Lyric.
///
/// <para>The key carries two constraints, <c>K :: [Hashable&lt;K&gt;, Equatable&lt;K&gt;]</c>, separate
/// because Lyric has no interface inheritance. This class therefore hangs directly on the constraint
/// work and is the reason it was built.</para>
///
/// <para>THE TOMBSTONES ARE THE DELICATE PART. A deleted slot must not break the probe chain: were it
/// simply "empty", a <c>get</c> would no longer find any key lying BEHIND the gap — and silently, since
/// the map then merely reports <c>null</c>.</para>
/// </summary>
public class MapTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static long Run(string source)
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
        Assert.False(de.HasErrors, "source did not compile: " + writer);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);
        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;
    }

    private const string Head = "import std.collections { Map };\n";

    // ------------------------------------------------------------------ Grundlagen

    [Fact]
    public void A_map_starts_empty() =>
        Assert.Equal(0, Run(Head + "fn main(): int { return Map<string, int>.empty().length(); }"));

    [Fact]
    public void Set_and_get_round_trip() =>
        Assert.Equal(7, Run(Head + """
            fn main(): int {
                let m = Map<string, int>.empty();
                m.set("a", 7);
                return m.get("a") ?? -1;
            }
            """));

    [Fact]
    public void A_missing_key_is_null() =>
        Assert.Equal(-1, Run(Head + """
            fn main(): int {
                let m = Map<string, int>.empty();
                m.set("a", 7);
                return m.get("b") ?? -1;
            }
            """));

    [Fact]
    public void Setting_an_existing_key_overwrites_instead_of_adding() =>
        // The length must NOT change; otherwise every overwrite would be a leak.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let m = Map<string, int>.empty();
                m.set("a", 1);
                m.set("a", 2);
                if (m.length() == 1 && (m.get("a") ?? -1) == 2) { return 1; }
                return 0;
            }
            """));

    [Fact]
    public void Remove_returns_the_old_value_and_shrinks() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let m = Map<string, int>.empty();
                m.set("a", 5);
                let alt = m.remove("a") ?? -1;
                if (alt == 5 && m.length() == 0 && !m.containsKey("a")) { return 1; }
                return 0;
            }
            """));

    [Fact]
    public void Removing_a_missing_key_is_null_and_changes_nothing() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let m = Map<string, int>.empty();
                m.set("a", 5);
                if (m.remove("weg") == null && m.length() == 1) { return 1; }
                return 0;
            }
            """));

    // ------------------------------------------------------- growth and tombstones

    /// <summary>
    /// Growth across several doublings: EVERYTHING has to stay findable.
    ///
    /// <para>While growing, the entries are reinserted rather than copied — the slots hang on the
    /// capacity. A fault there loses entries silently.</para>
    /// </summary>
    [Fact]
    public void Everything_survives_several_resizes() =>
        Assert.Equal(0, Run(Head + """
            fn main(): int {
                let m = Map<int, int>.empty();
                var i = 0;
                while (i < 200) { m.set(i, i * i); i = i + 1; }

                var falsch = 0;
                i = 0;
                while (i < 200) {
                    if ((m.get(i) ?? -1) != i * i) { falsch = falsch + 1; }
                    i = i + 1;
                }
                return falsch;
            }
            """));

    /// <summary>
    /// The test the tombstones turn on, and it checks in both directions.
    ///
    /// <para>Nothing lost behind a tombstone, and nothing removed still findable. A test looking only for
    /// what is missing never finds what is left over — the lesson from the first <c>List&lt;T&gt;</c>
    /// version, whose growth test left out exactly this second direction.</para>
    /// </summary>
    [Fact]
    public void Deleted_slots_do_not_break_the_probe_chain() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let m = Map<int, int>.empty();
                var i = 0;
                while (i < 200) { m.set(i, i * i); i = i + 1; }

                // Remove every second one: a hundred tombstones in the middle of the chains.
                i = 0;
                while (i < 200) { m.remove(i); i = i + 2; }

                var verloren = 0;
                i = 1;
                while (i < 200) {
                    if ((m.get(i) ?? -1) != i * i) { verloren = verloren + 1; }
                    i = i + 2;
                }

                var untot = 0;
                i = 0;
                while (i < 200) {
                    if (m.containsKey(i)) { untot = untot + 1; }
                    i = i + 2;
                }

                if (m.length() == 100 && verloren == 0 && untot == 0) { return 1; }
                return 0;
            }
            """));

    [Fact]
    public void Tombstones_are_reused_instead_of_growing_forever() =>
        // Three rounds of filling up and half emptying. Were tombstones not reused, the table would grow
        // without bound although it never holds more than 200 entries.
        Assert.Equal(200, Run(Head + """
            fn main(): int {
                let m = Map<int, int>.empty();
                var runde = 0;
                while (runde < 3) {
                    var j = 0;
                    while (j < 200) { m.set(j, j); j = j + 1; }
                    j = 0;
                    while (j < 200) { m.remove(j); j = j + 2; }
                    runde = runde + 1;
                }
                var i = 0;
                while (i < 200) { m.set(i, i); i = i + 1; }
                return m.length();
            }
            """));

    // ------------------------------------------------------------------ Nutzertypen

    [Fact]
    public void A_user_type_works_as_a_key() =>
        // What the constraints were built for: 'Point' satisfies both of them itself.
        Assert.Equal(1, Run("""
            import std.collections { Map };
            import std.core { Hashable, Equatable };

            pub struct Punkt :: [Equatable<Punkt>, Hashable<Punkt>] {
                x: int,
                y: int,
                fn equals(other: Punkt): bool { return this.x == other.x && this.y == other.y; }
                fn hash(): int { return this.x * 31 + this.y; }
            }

            fn main(): int {
                let m = Map<Punkt, int>.empty();
                m.set(Punkt { x = 1, y = 2 }, 42);
                let gefunden = m.get(Punkt { x = 1, y = 2 }) ?? -1;
                let fehlt = m.get(Punkt { x = 9, y = 9 }) ?? -1;
                if (gefunden == 42 && fehlt == -1) { return 1; }
                return 0;
            }
            """));

    /// <summary>
    /// A deliberately WORTHLESS hash: every key lands in the same starting slot.
    ///
    /// <para>The map still has to keep them apart. That checks the probing in isolation from the
    /// distribution — with a good hash a fault in the collision handling would occur rarely and by
    /// accident.</para>
    /// </summary>
    [Fact]
    public void Colliding_keys_stay_apart() =>
        Assert.Equal(1, Run("""
            import std.collections { Map };
            import std.core { Hashable, Equatable };

            pub struct Schlecht :: [Equatable<Schlecht>, Hashable<Schlecht>] {
                n: int,
                fn equals(other: Schlecht): bool { return this.n == other.n; }
                fn hash(): int { return 0; }
            }

            fn main(): int {
                let m = Map<Schlecht, int>.empty();
                var i = 0;
                while (i < 50) { m.set(Schlecht { n = i }, i * 3); i = i + 1; }

                var falsch = 0;
                i = 0;
                while (i < 50) {
                    if ((m.get(Schlecht { n = i }) ?? -1) != i * 3) { falsch = falsch + 1; }
                    i = i + 1;
                }
                if (m.length() == 50 && falsch == 0) { return 1; }
                return 0;
            }
            """));

    [Fact]
    public void A_negative_hash_does_not_break_the_slot_computation() =>
        // FNV-1a regularly yields negative values, and '%' follows the sign of the dividend in Lyric.
        // Strings are therefore the case that panics immediately without taking the absolute value.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let m = Map<string, int>.empty();
                m.set("hallo", 1);
                m.set("welt", 2);
                m.set("noch einer mit laengerem text", 3);
                if (m.length() == 3 && (m.get("welt") ?? -1) == 2) { return 1; }
                return 0;
            }
            """));
}
