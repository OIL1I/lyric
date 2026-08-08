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
/// `sortList` und `sortListBy` — Bottom-up Merge Sort, in Lyric geschrieben (M8b/S4).
///
/// <para><b>Stabilität ist der Grund für die Algorithmenwahl</b>, und sie ist die einzige
/// Eigenschaft, die man an einer sortierten Liste von Zahlen nicht ablesen kann. Der Test
/// <c>Equal_elements_keep_their_input_order</c> ist deshalb der wichtigste hier: ohne ihn wäre
/// ein instabiler Algorithmus grün.</para>
///
/// <para>Quicksort wäre schneller, ist aber instabil und bei sortierter Eingabe quadratisch — für
/// eine Standardbibliothek die falsche Überraschung. Dieselbe Wahl treffen Python, Java, C++
/// (`stable_sort`) und Go (`sort.Stable`).</para>
/// </summary>
public class SortTests
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

    private const string Head = """
        import std.collections { List, emptyList, sortList, sortListBy };

        // 1, wenn aufsteigend sortiert.
        fn istSortiert(xs: List<int>): int {
            var i = 1;
            while (i < xs.length()) {
                if (xs.get(i - 1) > xs.get(i)) { return 0; }
                i = i + 1;
            }
            return 1;
        }

        fn ausZahlen(werte: int[]): List<int> {
            let xs = emptyList<int>();
            for (v in werte) { xs.push(v); }
            return xs;
        }

        """;

    // ------------------------------------------------------------------ Grundfälle

    [Theory]
    [InlineData("[]")]
    [InlineData("[1]")]
    [InlineData("[2, 1]")]
    [InlineData("[5, 1, 4, 1, 9, 2, 6]")]
    [InlineData("[1, 2, 3, 4, 5]")]          // bereits sortiert
    [InlineData("[5, 4, 3, 2, 1]")]          // genau falsch herum
    [InlineData("[7, 7, 7, 7]")]             // alle gleich
    [InlineData("[3, -1, 0, -7, 2]")]        // negative
    public void Sorting_produces_an_ordered_list(string literal) =>
        Assert.Equal(1, Run(Head + $$"""
            fn main(): int {
                let xs = ausZahlen({{literal}});
                sortList(xs);
                return istSortiert(xs);
            }
            """));

    [Fact]
    public void Sorting_keeps_every_element() =>
        // Sortiert und vollzaehlig sind zwei Aussagen. Eine Sortierung, die Elemente verliert oder
        // verdoppelt, kann trotzdem sortiert sein — die Summe faengt beides.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let xs = ausZahlen([5, 1, 4, 1, 9, 2, 6]);
                var vorher = 0;
                var i = 0;
                while (i < xs.length()) { vorher = vorher + xs.get(i); i = i + 1; }

                sortList(xs);

                var nachher = 0;
                i = 0;
                while (i < xs.length()) { nachher = nachher + xs.get(i); i = i + 1; }

                if (xs.length() == 7 && vorher == nachher) { return 1; }
                return 0;
            }
            """));

    [Fact]
    public void A_longer_list_crosses_several_merge_widths() =>
        // 100 Elemente in absteigender Reihenfolge: die Bottom-up-Schleife laeuft ueber die
        // Breiten 1, 2, 4, …, 64, 128. Kurze Listen pruefen die aeusseren Runden nie.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let xs = emptyList<int>();
                var i = 100;
                while (i > 0) { xs.push(i); i = i - 1; }
                sortList(xs);
                if (istSortiert(xs) == 1 && xs.get(0) == 1 && xs.get(99) == 100) { return 1; }
                return 0;
            }
            """));

    // ------------------------------------------------------------------ Stabilität

    /// <summary>
    /// <b>Der wichtigste Test dieser Datei.</b>
    ///
    /// <para>Gleiche Elemente behalten ihre Eingabereihenfolge. An einer sortierten Zahlenliste
    /// ist das nicht ablesbar — deshalb tragen die Einträge hier eine Marke, die nicht in den
    /// Vergleich eingeht.</para>
    ///
    /// <para>Ohne diesen Test wäre ein instabiler Algorithmus grün, und die ganze Begründung für
    /// Merge Sort statt Quicksort hinfällig.</para>
    /// </summary>
    [Fact]
    public void Equal_elements_keep_their_input_order() =>
        Assert.Equal(1, Run("""
            import std.collections { emptyList, sortListBy };

            pub struct Eintrag { schluessel: int, marke: int }

            fn main(): int {
                let xs = emptyList<Eintrag>();
                xs.push(Eintrag { schluessel = 2, marke = 1 });
                xs.push(Eintrag { schluessel = 1, marke = 2 });
                xs.push(Eintrag { schluessel = 2, marke = 3 });
                xs.push(Eintrag { schluessel = 1, marke = 4 });
                xs.push(Eintrag { schluessel = 2, marke = 5 });

                sortListBy(xs, (a: Eintrag, b: Eintrag) => a.schluessel < b.schluessel);

                // Erwartet: 1/2, 1/4, 2/1, 2/3, 2/5 — innerhalb jedes Schluessels aufsteigende
                // Marken, also die urspruengliche Reihenfolge.
                var i = 1;
                while (i < xs.length()) {
                    let vorher = xs.get(i - 1);
                    let jetzt = xs.get(i);
                    if (vorher.schluessel == jetzt.schluessel && vorher.marke > jetzt.marke) {
                        return 0;
                    }
                    i = i + 1;
                }
                if (xs.get(0).marke == 2 && xs.get(4).marke == 5) { return 1; }
                return 0;
            }
            """));

    // ------------------------------------------------------------------ Komparator

    [Fact]
    public void A_custom_comparator_reverses_the_order() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let xs = ausZahlen([3, 1, 2]);
                sortListBy(xs, (a: int, b: int) => a > b);
                if (xs.get(0) == 3 && xs.get(1) == 2 && xs.get(2) == 1) { return 1; }
                return 0;
            }
            """));

    [Fact]
    public void A_comparator_may_sort_by_a_field() =>
        Assert.Equal(1, Run("""
            import std.collections { emptyList, sortListBy };

            pub struct Person { name: string, alter: int }

            fn main(): int {
                let xs = emptyList<Person>();
                xs.push(Person { name = "c", alter = 30 });
                xs.push(Person { name = "a", alter = 10 });
                xs.push(Person { name = "b", alter = 20 });

                sortListBy(xs, (p: Person, q: Person) => p.alter < q.alter);

                if (xs.get(0).alter == 10 && xs.get(2).alter == 30) { return 1; }
                return 0;
            }
            """));

    [Fact]
    public void A_comparator_that_always_says_false_leaves_the_order_alone() =>
        // Ein 'less', das nie 'true' liefert, macht alle Elemente gleichwertig. Ein stabiler
        // Algorithmus muss dann die Eingabereihenfolge unveraendert lassen — und darf vor allem
        // nicht in eine Endlosschleife laufen.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let xs = ausZahlen([3, 1, 2]);
                sortListBy(xs, (a: int, b: int) => false);
                if (xs.get(0) == 3 && xs.get(1) == 1 && xs.get(2) == 2) { return 1; }
                return 0;
            }
            """));

    /// <summary>
    /// Ein Lambda mit einem <b>Typ-Parameter</b> als Parametertyp, innerhalb einer generischen
    /// Funktion.
    ///
    /// <para>Genau das ist <c>sortList</c>: es reicht <c>(a: T, b: T) =&gt; a.compare(b) &lt; 0</c>
    /// an <c>sortListBy</c> weiter. Das Lowering brach dabei ab („type parameter 'T' is not
    /// supported") — ein Lambda-Lowerer bekam grundsätzlich <c>NoSubstitution</c>, auch innerhalb
    /// einer monomorphisierten Instanz.</para>
    ///
    /// <para>Ein Lambda ist kein eigener generischer Kontext; es erbt den seines Rumpfes. Dass es
    /// als eigene Funktion gelowert wird (ADR-018), ist eine Implementierungsentscheidung und darf
    /// an den Typen nichts ändern.</para>
    /// </summary>
    [Fact]
    public void A_lambda_may_use_the_enclosing_type_parameter() =>
        Assert.Equal(1, Run("""
            import std.core { Ordered };

            pub fn kleiner<T>(a: T, b: T, f: fn(T, T) -> bool): bool { let g = f; return g(a, b); }

            pub fn natuerlich<T :: [Ordered<T>]>(a: T, b: T): bool {
                return kleiner<T>(a, b, (x: T, y: T) => x.compare(y) < 0);
            }

            fn main(): int {
                if (natuerlich(1, 2) && !natuerlich(2, 1)) { return 1; }
                return 0;
            }
            """));
}
