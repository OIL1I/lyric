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
/// `Set&lt;T&gt;` und die Nachträge an `Map` und `List` (M8b/S4).
///
/// <para><b>`Set` ist nicht als `Map&lt;T, bool&gt;` gebaut</b>, obwohl das kürzer wäre: das
/// verschwendet ein Array, und `add(x)` hieße `set(x, true)` — eine Schreibweise, die an jeder
/// Aufrufstelle erklärt werden müsste. Die Duplikation der Sondierungslogik ist der ehrlichere
/// Preis.</para>
///
/// <para>Die Iterations-Reihenfolge hängt an den Hashes und ist <b>unspezifiziert</b>. Die Tests
/// hier prüfen deshalb Summen und Anzahlen, nie eine Position — ein Test, der sich auf die
/// heutige Hash-Reihenfolge verlässt, wäre eine Zusage, die die Bibliothek nicht macht.</para>
/// </summary>
public class SetTests
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

    private const string Head =
        "import std.collections { Set, emptySet, union, intersect, difference, isSubset };\n";

    // ------------------------------------------------------------------ Grundlagen

    [Fact]
    public void A_set_starts_empty() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let s = emptySet<int>();
                return if (s.length() == 0 && s.isEmpty()) 1 else 0;
            }
            """));

    [Fact]
    public void Add_reports_whether_the_value_was_new() =>
        // Der Rueckgabewert ist nicht Zierde: "war das schon drin?" ist die Frage, fuer die man
        // sonst ein 'contains' davorsetzt — und damit zweimal sondiert.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let s = emptySet<int>();
                let ersteMal = s.add(7);
                let zweiteMal = s.add(7);
                return if (ersteMal && !zweiteMal && s.length() == 1) 1 else 0;
            }
            """));

    [Fact]
    public void Remove_reports_whether_the_value_was_there() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let s = emptySet<int>();
                s.add(7);
                let weg = s.remove(7);
                let nochmal = s.remove(7);
                return if (weg && !nochmal && s.isEmpty()) 1 else 0;
            }
            """));

    [Fact]
    public void Everything_survives_resizing() =>
        // 200 Werte ueber mehrere Verdopplungen; danach muessen alle noch da sein und keiner
        // doppelt gezaehlt.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let s = emptySet<int>();
                var i = 0;
                while (i < 200) { s.add(i); i = i + 1; }

                var fehlend = 0;
                i = 0;
                while (i < 200) {
                    if (!s.contains(i)) { fehlend = fehlend + 1; }
                    i = i + 1;
                }
                return if (s.length() == 200 && fehlend == 0) 1 else 0;
            }
            """));

    [Fact]
    public void Iteration_visits_every_element_exactly_once() =>
        // Summe UND Anzahl: die Summe allein faende ein doppelt besuchtes Element nicht, wenn
        // dafuer ein anderes fehlte.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let s = emptySet<int>();
                var i = 1;
                while (i <= 10) { s.add(i); i = i + 1; }

                var summe = 0;
                var anzahl = 0;
                for (v in s.iter()) { summe = summe + v; anzahl = anzahl + 1; }

                return if (summe == 55 && anzahl == 10) 1 else 0;
            }
            """));

    // ------------------------------------------------------------ Mengenoperationen

    [Fact]
    public void Union_contains_both_sides_without_duplicates() =>
        Assert.Equal(4, Run(Head + """
            fn main(): int {
                let a = emptySet<int>();
                a.add(1); a.add(2);
                let b = emptySet<int>();
                b.add(2); b.add(3); b.add(4);
                return union(a, b).length();
            }
            """));

    [Fact]
    public void Intersect_keeps_only_what_both_have() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let a = emptySet<int>();
                a.add(1); a.add(2);
                let b = emptySet<int>();
                b.add(2); b.add(3);
                let s = intersect(a, b);
                return if (s.length() == 1 && s.contains(2)) 1 else 0;
            }
            """));

    [Fact]
    public void Intersect_is_symmetric_regardless_of_size() =>
        // Die Implementierung laeuft ueber die KLEINERE Menge. Ohne diesen Test bliebe ungeprueft,
        // ob beide Richtungen dasselbe liefern — der Tausch ist genau die Stelle, an der man sich
        // vertut.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let klein = emptySet<int>();
                klein.add(5);
                let gross = emptySet<int>();
                var i = 0;
                while (i < 20) { gross.add(i); i = i + 1; }

                let a = intersect(klein, gross);
                let b = intersect(gross, klein);
                return if (a.length() == 1 && b.length() == 1 && a.contains(5) && b.contains(5))
                    1 else 0;
            }
            """));

    [Fact]
    public void Difference_removes_what_the_other_has() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let a = emptySet<int>();
                a.add(1); a.add(2); a.add(3);
                let b = emptySet<int>();
                b.add(2);
                let s = difference(a, b);
                return if (s.length() == 2 && s.contains(1) && s.contains(3) && !s.contains(2))
                    1 else 0;
            }
            """));

    [Fact]
    public void The_operations_do_not_modify_their_arguments() =>
        // Alle drei liefern eine NEUE Menge. Eine, die still ihr Argument aendert, waere in einer
        // Kette nicht mehr benutzbar.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let a = emptySet<int>();
                a.add(1); a.add(2);
                let b = emptySet<int>();
                b.add(2); b.add(3);

                union(a, b);
                intersect(a, b);
                difference(a, b);

                return if (a.length() == 2 && b.length() == 2) 1 else 0;
            }
            """));

    [Fact]
    public void IsSubset_checks_containment_in_both_directions() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let klein = emptySet<int>();
                klein.add(1);
                let gross = emptySet<int>();
                gross.add(1); gross.add(2);
                return if (isSubset(klein, gross) && !isSubset(gross, klein)) 1 else 0;
            }
            """));

    // --------------------------------------------------- Nachträge an Map und List

    /// <summary>
    /// Eine `Map` lässt sich durchlaufen.
    ///
    /// <para>Bis zu diesem Slice ging das <b>nicht</b>: man konnte etwas hineinlegen und gezielt
    /// herausholen, aber nicht wissen, was drin ist. `keys` und `values` fehlten, weil sie am
    /// selben Compiler-Fehler hingen wie `Set.iter()`.</para>
    ///
    /// <para>`entries(): Iterator&lt;(K, V)&gt;` fehlt weiterhin — ein Tupel als Typargument eines
    /// generischen Interfaces ist nicht lowerbar.</para>
    /// </summary>
    [Fact]
    public void A_map_can_be_walked_by_keys_and_values() =>
        Assert.Equal(1, Run("""
            import std.collections { emptyMap, keys, values };

            fn main(): int {
                let m = emptyMap<string, int>();
                m.set("a", 1); m.set("b", 2); m.set("c", 3);

                var anzahlKeys = 0;
                for (k in keys(m)) { anzahlKeys = anzahlKeys + 1; }

                var summe = 0;
                var anzahlValues = 0;
                for (v in values(m)) { summe = summe + v; anzahlValues = anzahlValues + 1; }

                return if (anzahlKeys == 3 && anzahlValues == 3 && summe == 6) 1 else 0;
            }
            """));

    [Fact]
    public void A_removed_entry_does_not_show_up_in_the_walk() =>
        // Die Grabsteine duerfen beim Durchlaufen nicht auftauchen — 'states[i] == 1' ist die
        // Bedingung, und '!= 0' waere die naheliegende falsche.
        Assert.Equal(1, Run("""
            import std.collections { emptyMap, keys };

            fn main(): int {
                let m = emptyMap<int, int>();
                var i = 0;
                while (i < 10) { m.set(i, i); i = i + 1; }
                i = 0;
                while (i < 10) { m.remove(i); i = i + 2; }

                var gesehen = 0;
                for (k in keys(m)) { gesehen = gesehen + 1; }
                return if (gesehen == 5 && m.length() == 5) 1 else 0;
            }
            """));

    [Fact]
    public void A_list_knows_whether_it_is_empty() =>
        // Map und Set hatten das seit ihrem ersten Tag, List nicht.
        Assert.Equal(1, Run("""
            import std.collections { emptyList };

            fn main(): int {
                let xs = emptyList<int>();
                let vorher = xs.isEmpty();
                xs.push(1);
                return if (vorher && !xs.isEmpty()) 1 else 0;
            }
            """));

    [Fact]
    public void A_list_can_be_searched() =>
        // Freie Funktionen und keine Methoden: sie brauchen 'Equatable<T>', und dieser Constraint
        // auf der KLASSE machte 'List<T>' fuer jeden Typ ohne Gleichheit unbrauchbar — auch dort,
        // wo niemand sucht.
        Assert.Equal(1, Run("""
            import std.collections { emptyList, listContains, listIndexOf };

            fn main(): int {
                let xs = emptyList<int>();
                xs.push(10); xs.push(20); xs.push(30);

                let hat = listContains(xs, 20);
                let fehlt = listContains(xs, 99);
                let pos = listIndexOf(xs, 30) ?? -1;
                let keine = listIndexOf(xs, 99) ?? -1;

                return if (hat && !fehlt && pos == 2 && keine == -1) 1 else 0;
            }
            """));

    /// <summary>
    /// Der Compiler-Fehler, der `Set` blockiert hat — auf zwölf Zeilen.
    ///
    /// <para>Eine Klasse mit <b>constraintem</b> Typ-Parameter, deren Methode `Iterator&lt;T&gt;`
    /// liefert, war nicht lowerbar — aber erst, wenn sie <i>instanziiert</i> wird. `List&lt;T&gt;`
    /// hat dieselbe Methode und funktionierte, weil ihr `T` keinen Constraint trägt.</para>
    ///
    /// <para>`LowerWithOwner` war eine Teilkopie der Typ-Auflösung und zum <b>dritten</b> Mal zu
    /// kurz: erst der nackte Fall, dann `?T`, dann `T[]` — und ein generischer Typ als
    /// Rückgabetyp fehlte immer noch.</para>
    /// </summary>
    [Fact]
    public void A_constrained_generic_class_may_return_an_interface_instance() =>
        Assert.Equal(3, Run("""
            import std.iter { Iterator, ArrayIterator };
            import std.core { Hashable, Equatable };

            pub class Box<T :: [Hashable<T>, Equatable<T>]> {
                xs: T[],
                pub fn iter(): Iterator<T> {
                    return ArrayIterator<T> { source = this.xs, index = 0 };
                }
            }

            fn main(): int {
                let b = Box<int> { xs = [1, 2, 3] };
                var n = 0;
                for (v in b.iter()) { n = n + 1; }
                return n;
            }
            """));
}
