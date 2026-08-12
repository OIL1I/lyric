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
/// `std.collections` — `Indexable&lt;T&gt;` und `List&lt;T&gt;` (M8/S5).
///
/// <para><b>`List&lt;T&gt;` ist in Lyric geschrieben, nicht nativ.</b> Die ROADMAP sah einen Hook
/// auf `System.Collections.Generic.List&lt;&gt;` vor; dagegen sprach, dass Natives monomorph
/// registriert werden — ein generischer bräuchte eine Marshalling-Schicht, und die gehört zu
/// M10. Dass eine Stdlib ihre eigenen Container ausdrücken kann, ist ohnehin die interessantere
/// Aussage.</para>
///
/// <para><b>Das Backing ist `(?T)[]`</b>, damit ein Slot leerbar ist: `pop` gibt seinen Wert
/// wirklich frei, statt ihn nur hinter `count` verschwinden zu lassen. Nebenbei löst das die
/// Erzeugung — ein `T[]` der Länge n bräuchte n Werte vom Typ T, und Lyric hat kein `default(T)`;
/// für `?T` gibt es einen.</para>
///
/// <para><b>Die erste Fassung war an zwei Stellen falsch</b>, und diese Datei hat es nicht
/// gemerkt: `get` prüfte gegen `data.length` statt gegen `count` und gab damit Reste aus dem
/// Verdoppeln zurück, und `pop` ließ seinen Wert im Slot stehen. Der Wachstums-Test prüfte nur,
/// dass nichts <b>fehlt</b> — nicht, dass nichts <b>zu viel</b> da ist. Die fünf Tests unter
/// „Grenzen und Freigabe" schließen genau diese Richtung.</para>
/// </summary>
public class CollectionTests
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

    private const string Head = "import std.collections { List, emptyList };\n";

    // ------------------------------------------------------------------ List<T>

    [Fact]
    public void A_list_starts_empty() =>
        Assert.Equal(0, Run(Head + "fn main(): int { return emptyList<int>().length(); }"));

    [Fact]
    public void Push_appends_and_counts() =>
        Assert.Equal(3, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                xs.push(1);
                xs.push(2);
                xs.push(3);
                return xs.length();
            }
            """));

    [Fact]
    public void Growth_preserves_every_element()
    {
        // DER Test des Wachstums. Bei 100 Elementen verdoppelt sich das Array siebenmal; würde
        // dabei ein Element verlorengehen oder überschrieben, stimmte die Summe nicht. Ein Test
        // mit drei Elementen bliebe grün, auch wenn das Verdoppeln kaputt wäre.
        Assert.Equal(4950, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                var i = 0;
                while (i < 100) { xs.push(i); i = i + 1; }

                var sum = 0;
                var k = 0;
                while (k < xs.length()) { sum = sum + xs.get(k); k = k + 1; }
                return sum;
            }
            """));
    }

    [Fact]
    public void Pop_returns_the_last_value_and_shrinks() =>
        Assert.Equal(21, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                xs.push(1);
                xs.push(2);
                let last = xs.pop() ?? 0;
                return last * 10 + xs.length();
            }
            """));

    [Fact]
    public void Pop_on_an_empty_list_is_null_not_a_panic() =>
        // Eine leere Liste ist ein gewöhnlicher Zustand, kein Programmierfehler — anders als ein
        // Index daneben. Deshalb `?T` und kein `panic`.
        Assert.Equal(7, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                return xs.pop() ?? 7;
            }
            """));

    [Fact]
    public void A_list_of_strings_works_too() =>
        // Zweite Instanziierung: mit nur einer bliebe der Test auch grün, wenn die
        // Monomorphisierung den Elementtyp ignorierte.
        Assert.Equal(2, Run("""
            import std.collections { List, emptyList };
            fn main(): int {
                let xs = emptyList<string>();
                xs.push("a");
                xs.push("b");
                return xs.length();
            }
            """));

    // ------------------------------------------------------------------ Indexable<T>

    [Fact]
    public void A_list_can_be_read_with_brackets() =>
        // `[i]` auf einem Nicht-Array läuft über `Indexable<T>.get` — dieselbe Arbeitsteilung
        // wie `for-in` über `Iterator<T>`. Der Compiler kennt genau EINE eingebaute indizierbare
        // Form, das Array.
        Assert.Equal(20, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                xs.push(10);
                xs.push(20);
                return xs[1];
            }
            """));

    [Fact]
    public void A_list_can_be_written_with_brackets() =>
        // Das geht nur, weil `let` seit ADR-020 den Namen bindet und nicht den Inhalt. Unter der
        // alten Regel hätte `Indexable<T>` eine Sonderregel nachbilden müssen.
        Assert.Equal(5, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                xs.push(10);
                xs[0] = 5;
                return xs[0];
            }
            """));

    [Fact]
    public void An_array_still_uses_the_builtin_path() =>
        // Die Gegenprobe: der Indexable-Pfad darf die eingebaute Array-Indizierung nicht
        // übernommen haben. Ein Array implementiert `Indexable<T>` nicht — es ist die eingebaute
        // Form, und `ldelem` bleibt ein Array-Zugriff ohne Methodenaufruf.
        Assert.Equal(9, Run("fn main(): int { let xs = [1, 9, 3]; return xs[1]; }"));

    [Fact]
    public void A_user_type_can_implement_Indexable() =>
        // Nicht nur die Stdlib: das Interface steht jedem offen. Ohne diesen Test wäre nicht
        // festgehalten, dass `[i]` wirklich am Interface hängt und nicht an `List<T>`.
        Assert.Equal(42, Run("""
            import std.collections { Indexable };

            class Doubler :: [Indexable<int>] {
                base: int,
                pub fn get(index: int): int { return this.base * index; }
                pub mut fn set(index: int, value: int): void { this.base = value; }
            }

            fn main(): int {
                let d = Doubler { base = 21 };
                return d[2];
            }
            """));

    // ------------------------------------------------------------------ Grenzen und Freigabe

    // Diese fuenf Tests gab es in der ersten Fassung NICHT, und deshalb ueberlebten dort zwei
    // Fehler: 'get' pruefte gegen 'data.length' statt gegen 'count' und gab Reste aus dem
    // Verdoppeln zurueck, und 'pop' liess seinen Wert im Slot stehen. Der Wachstums-Test oben
    // pruefte nur, dass nichts FEHLT — nicht, dass nichts ZU VIEL da ist.

    [Fact]
    public void Reading_past_the_end_panics_even_when_capacity_is_larger()
    {
        // Nach drei push ist die Kapazitaet 4: Index 3 liegt innerhalb des Arrays, aber
        // ausserhalb der Liste. Genau hier gab die alte Fassung einen Rest zurueck.
        var panic = Assert.Throws<LyricPanic>(() => Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                xs.push(10);
                xs.push(20);
                xs.push(30);
                return xs[3];
            }
            """));

        Assert.Contains("out of range", panic.Message);
    }

    [Fact]
    public void A_popped_value_is_gone_not_just_hidden() =>
        // 'pop' leert den Slot. Sonst haelt die Liste das Objekt am Leben, und ein 'get' an
        // dieser Stelle liest es zurueck, obwohl es entfernt ist.
        Assert.Throws<LyricPanic>(() => Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                xs.push(10);
                xs.push(20);
                let v = xs.pop();
                return xs[1];
            }
            """));

    [Fact]
    public void A_negative_index_panics() =>
        Assert.Throws<LyricPanic>(() => Run(Head + """
            fn main(): int { let xs = emptyList<int>(); xs.push(1); return xs[0 - 1]; }
            """));

    [Fact]
    public void Push_after_pop_overwrites_instead_of_appending() =>
        // 'count' ist die Wahrheit, nicht 'data.length'. Waere es umgekehrt, wuechse die Liste
        // nach einem pop an der falschen Stelle weiter.
        Assert.Equal(99, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                xs.push(10);
                xs.push(20);
                let v = xs.pop();
                xs.push(99);
                return xs[1] + (xs.length() - 2);
            }
            """));

    [Fact]
    public void Capacity_grows_by_doubling_and_shrinks_by_halving()
    {
        // Der Punkt, der die Datenstruktur-Entscheidung getragen hat: der Slack bleibt nicht
        // liegen. 100 Elemente -> Kapazitaet 128; nach 95 pop -> 5 Elemente, Kapazitaet 16.
        //
        // Die Schwelle liegt bei einem VIERTEL und nicht bei der Haelfte, damit eine Liste, die
        // um die Grenze pendelt, nicht bei jedem push/pop umkopiert.
        Assert.Equal(128, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                var i = 0;
                while (i < 100) { xs.push(i); i = i + 1; }
                return xs.capacity();
            }
            """));

        Assert.Equal(16, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                var i = 0;
                while (i < 100) { xs.push(i); i = i + 1; }
                var k = 0;
                while (k < 95) { let v = xs.pop(); k = k + 1; }
                return xs.capacity();
            }
            """));
    }

    [Fact]
    public void A_small_list_keeps_a_floor_capacity() =>
        // Unter vier Slots lohnt das Umkopieren nicht — der Gewinn waeren ein paar Dutzend Byte.
        Assert.Equal(4, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                xs.push(1);
                let v = xs.pop();
                return xs.capacity();
            }
            """));

    // ------------------------------------------------------------------ Iterable<T>

    [Fact]
    public void A_list_can_be_walked_with_for_in() =>
        // 'for-in' fragt VORWAERTS: erfuellt der Traeger 'Iterable<T>', wird 'iter()' gerufen.
        // Der Compiler sucht NICHT rueckwaerts nach einem Iterator, der die Liste als Quelle
        // nimmt — das waere bei zwei Kandidaten mehrdeutig und muesste alle sichtbaren Module
        // absuchen.
        Assert.Equal(6, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                xs.push(1);
                xs.push(2);
                xs.push(3);
                var sum = 0;
                for (x in xs) { sum = sum + x; }
                return sum;
            }
            """));

    [Fact]
    public void Two_loops_over_the_same_list_do_not_interfere() =>
        // DER Grund fuer die Zweiteilung. Waere 'List<T>' ihr eigener 'Iterator<T>', haette sie
        // einen eingebauten Fortschritt — die innere Schleife wuerde die aeussere weiterschieben,
        // und statt 9 kaeme 3 heraus. 'iter()' liefert bei jedem Aufruf einen frischen Cursor.
        Assert.Equal(9, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                xs.push(1);
                xs.push(2);
                xs.push(3);
                var n = 0;
                for (a in xs) {
                    for (b in xs) { n = n + 1; }
                }
                return n;
            }
            """));

    [Fact]
    public void An_empty_list_iterates_zero_times() =>
        Assert.Equal(0, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                var n = 0;
                for (x in xs) { n = n + 1; }
                return n;
            }
            """));

    [Fact]
    public void A_user_type_can_implement_Iterable() =>
        // Wie bei 'Indexable': das Interface steht jedem offen, nicht nur der Stdlib. Ohne
        // diesen Test haenge 'for-in' womoeglich an 'List<T>' statt am Interface.
        Assert.Equal(3, Run("""
            import std.iter { Iterator, Iterable };

            class Once :: [Iterator<int>] {
                done: bool,
                pub mut fn next(): ?int {
                    if (this.done) { return null; }
                    this.done = true;
                    return 3;
                }
            }

            class Source :: [Iterable<int>] {
                pub fn iter(): Iterator<int> { return Once { done = false }; }
            }

            fn main(): int {
                let s = Source { };
                var sum = 0;
                for (x in s) { sum = sum + x; }
                return sum;
            }
            """));

    [Fact]
    public void A_plain_iterator_still_works_directly() =>
        // Die Gegenprobe: der Iterable-Pfad darf den alten nicht verdraengt haben. Ein Wert, der
        // selbst 'Iterator<T>' erfuellt, wird weiterhin direkt benutzt.
        Assert.Equal(3, Run("""
            import std.iter { Iterator };

            class Once :: [Iterator<int>] {
                done: bool,
                pub mut fn next(): ?int {
                    if (this.done) { return null; }
                    this.done = true;
                    return 3;
                }
            }

            fn main(): int {
                let o = Once { done = false };
                var sum = 0;
                for (x in o) { sum = sum + x; }
                return sum;
            }
            """));

    // ------------------------------------------------------------------ clear und toArray
    //
    // Beide kamen am 2026-08-12 dazu. 'toArray' ist der interessantere Fall: sein Rueckgabetyp ist
    // 'T[]' und das Backing ein '(?T)[]', und zwischen beiden gibt es KEINE Umdeutung — '!' packt
    // einen einzelnen Wert aus, nicht ein Array elementweise. Die erste Fassung versuchte genau
    // das ('return result!;') und war LYR-SEM0005.

    [Fact]
    public void ToArray_copies_the_used_slots_and_nothing_more() =>
        // 3 Elemente, Summe 17 — Laenge UND Inhalt in einer Zahl, damit kein Teil davon
        // unbemerkt danebenliegen kann.
        Assert.Equal(3017, Run("""
            import std.collections;

            fn main(): int {
                var xs = collections.emptyList<int>();
                xs.push(3);
                xs.push(5);
                xs.push(9);

                let a = xs.toArray();
                return a.length * 1000 + a[0] + a[1] + a[2];
            }
            """));

    /// <summary>
    /// Die Laenge ist <c>count</c> und nicht <c>capacity</c>. Nach drei <c>push</c> stehen vier
    /// Slots bereit — ein Array mit vier Elementen waere der Fehler, den <c>get</c> schon einmal
    /// gemacht hat.
    /// </summary>
    [Fact]
    public void ToArray_uses_count_and_not_capacity() =>
        Assert.Equal(3, Run("""
            import std.collections;

            fn main(): int {
                var xs = collections.emptyList<int>();
                xs.push(1);
                xs.push(2);
                xs.push(3);
                return xs.toArray().length;
            }
            """));

    /// <summary>Die leere Liste hat kein erstes Element, aus dem sich ein <c>T[]</c> bauen liesse
    /// — sie wird vorher abgefangen. Ohne diesen Test bliebe genau der Zweig ungeprueft.</summary>
    [Fact]
    public void ToArray_on_an_empty_list_is_an_empty_array() =>
        Assert.Equal(0, Run("""
            import std.collections;

            fn main(): int {
                let xs = collections.emptyList<int>();
                return xs.toArray().length;
            }
            """));

    /// <summary>Die Kopie ist eine Kopie: wer sie aendert, aendert die Liste nicht.</summary>
    [Fact]
    public void ToArray_returns_a_copy() =>
        Assert.Equal(1, Run("""
            import std.collections;

            fn main(): int {
                var xs = collections.emptyList<int>();
                xs.push(1);

                let a = xs.toArray();
                a[0] = 99;
                return xs.get(0);
            }
            """));

    [Fact]
    public void Clear_empties_the_list() =>
        Assert.Equal(0, Run("""
            import std.collections;

            fn main(): int {
                var xs = collections.emptyList<int>();
                xs.push(1);
                xs.push(2);
                xs.clear();
                return xs.length();
            }
            """));

    /// <summary>
    /// <c>clear</c> gibt das Backing wirklich frei und laesst es nicht nur hinter <c>count</c>
    /// stehen — dieselbe Zusicherung wie bei <c>pop</c>, und aus demselben Grund: sonst hielte die
    /// Liste jedes je eingefuegte Objekt am Leben.
    /// </summary>
    [Fact]
    public void Clear_releases_the_backing_array() =>
        Assert.Equal(0, Run("""
            import std.collections;

            fn main(): int {
                var xs = collections.emptyList<int>();
                xs.push(1);
                xs.push(2);
                xs.push(3);
                xs.clear();
                return xs.capacity();
            }
            """));

    /// <summary>Und danach ist sie wieder benutzbar — ein geleertes Backing darf kein Sonderfall
    /// fuer <c>push</c> sein.</summary>
    [Fact]
    public void A_cleared_list_still_grows() =>
        Assert.Equal(7, Run("""
            import std.collections;

            fn main(): int {
                var xs = collections.emptyList<int>();
                xs.push(1);
                xs.clear();
                xs.push(7);
                return xs.get(0);
            }
            """));
}
