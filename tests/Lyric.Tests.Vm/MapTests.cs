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
/// `Map&lt;K, V&gt;` — Open Addressing mit linearer Sondierung, in Lyric geschrieben (M8b/S4).
///
/// <para>Der Schlüssel trägt zwei Constraints, <c>K :: [Hashable&lt;K&gt;, Equatable&lt;K&gt;]</c>,
/// getrennt weil Lyric keine Interface-Vererbung hat. Damit hängt diese Klasse direkt an ADR-024
/// und am M4-Constraint-Fix — sie ist der Grund, warum beide gebaut wurden.</para>
///
/// <para><b>Die Grabsteine sind der heikle Teil.</b> Ein gelöschter Slot darf die
/// Sondierungskette nicht unterbrechen: würde er einfach „leer", fände ein <c>get</c> jeden
/// Schlüssel nicht mehr, der <i>hinter</i> der Lücke liegt — und zwar lautlos, denn die Map
/// meldet dann bloß <c>null</c>.</para>
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

    private const string Head = "import std.collections { Map, emptyMap };\n";

    // ------------------------------------------------------------------ Grundlagen

    [Fact]
    public void A_map_starts_empty() =>
        Assert.Equal(0, Run(Head + "fn main(): int { return emptyMap<string, int>().length(); }"));

    [Fact]
    public void Set_and_get_round_trip() =>
        Assert.Equal(7, Run(Head + """
            fn main(): int {
                let m = emptyMap<string, int>();
                m.set("a", 7);
                return m.get("a") ?? -1;
            }
            """));

    [Fact]
    public void A_missing_key_is_null() =>
        Assert.Equal(-1, Run(Head + """
            fn main(): int {
                let m = emptyMap<string, int>();
                m.set("a", 7);
                return m.get("b") ?? -1;
            }
            """));

    [Fact]
    public void Setting_an_existing_key_overwrites_instead_of_adding() =>
        // Die Laenge darf sich NICHT aendern — sonst waere jedes Ueberschreiben ein Leck.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let m = emptyMap<string, int>();
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
                let m = emptyMap<string, int>();
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
                let m = emptyMap<string, int>();
                m.set("a", 5);
                if (m.remove("weg") == null && m.length() == 1) { return 1; }
                return 0;
            }
            """));

    // ------------------------------------------------------- Wachstum und Grabsteine

    /// <summary>
    /// Wachstum über mehrere Verdopplungen: <b>alles</b> muss wiederfindbar bleiben.
    ///
    /// <para>Beim Vergrößern werden die Einträge neu eingefügt und nicht kopiert — die Slots
    /// hängen an der Kapazität. Ein Fehler dort verliert Einträge lautlos.</para>
    /// </summary>
    [Fact]
    public void Everything_survives_several_resizes() =>
        Assert.Equal(0, Run(Head + """
            fn main(): int {
                let m = emptyMap<int, int>();
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
    /// <b>Der Test, an dem sich die Grabsteine entscheiden</b> — und er prüft in beide Richtungen.
    ///
    /// <para>Nichts hinter einem Grabstein verloren, und nichts Entferntes noch auffindbar. Ein
    /// Test, der nur nach Fehlendem sucht, findet Übriggebliebenes nie — die Lehre aus der ersten
    /// <c>List&lt;T&gt;</c>-Fassung, deren Wachstums-Test genau diese zweite Richtung ausließ.</para>
    /// </summary>
    [Fact]
    public void Deleted_slots_do_not_break_the_probe_chain() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let m = emptyMap<int, int>();
                var i = 0;
                while (i < 200) { m.set(i, i * i); i = i + 1; }

                // Jeden zweiten entfernen: 100 Grabsteine mitten in den Ketten.
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
        // Drei Runden auffuellen und halb leeren. Wuerden Grabsteine nicht wiederverwendet, waechst
        // die Tabelle unbegrenzt, obwohl nie mehr als 200 Eintraege drin sind.
        Assert.Equal(200, Run(Head + """
            fn main(): int {
                let m = emptyMap<int, int>();
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
        // Wofuer ADR-024 gebaut wurde: 'Punkt' erfuellt beide Constraints selbst.
        Assert.Equal(1, Run("""
            import std.collections { emptyMap };
            import std.core { Hashable, Equatable };

            pub struct Punkt :: [Equatable<Punkt>, Hashable<Punkt>] {
                x: int,
                y: int,
                fn equals(other: Punkt): bool { return this.x == other.x && this.y == other.y; }
                fn hash(): int { return this.x * 31 + this.y; }
            }

            fn main(): int {
                let m = emptyMap<Punkt, int>();
                m.set(Punkt { x = 1, y = 2 }, 42);
                let gefunden = m.get(Punkt { x = 1, y = 2 }) ?? -1;
                let fehlt = m.get(Punkt { x = 9, y = 9 }) ?? -1;
                if (gefunden == 42 && fehlt == -1) { return 1; }
                return 0;
            }
            """));

    /// <summary>
    /// Ein absichtlich <b>wertloser</b> Hash: alle Schlüssel landen im selben Startslot.
    ///
    /// <para>Die Map muss sie trotzdem auseinanderhalten. Das prüft die Sondierung isoliert von
    /// der Streuung — mit einem guten Hash würde ein Fehler in der Kollisionsbehandlung nur
    /// selten und zufällig auftreten.</para>
    /// </summary>
    [Fact]
    public void Colliding_keys_stay_apart() =>
        Assert.Equal(1, Run("""
            import std.collections { emptyMap };
            import std.core { Hashable, Equatable };

            pub struct Schlecht :: [Equatable<Schlecht>, Hashable<Schlecht>] {
                n: int,
                fn equals(other: Schlecht): bool { return this.n == other.n; }
                fn hash(): int { return 0; }
            }

            fn main(): int {
                let m = emptyMap<Schlecht, int>();
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
        // FNV-1a liefert regelmaessig negative Werte, und '%' folgt in Lyric dem Vorzeichen des
        // Dividenden. Strings sind deshalb der Fall, der ohne Betragsbildung sofort panict.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let m = emptyMap<string, int>();
                m.set("hallo", 1);
                m.set("welt", 2);
                m.set("noch einer mit laengerem text", 3);
                if (m.length() == 3 && (m.get("welt") ?? -1) == 2) { return 1; }
                return 0;
            }
            """));
}
