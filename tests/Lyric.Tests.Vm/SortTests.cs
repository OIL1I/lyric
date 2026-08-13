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
/// `sortList` and `sortListBy` — a bottom-up merge sort, written in Lyric.
///
/// <para>STABILITY IS THE REASON FOR THE CHOICE OF ALGORITHM, and it is the one property that cannot
/// be read off a sorted list of numbers. <c>Equal_elements_keep_their_input_order</c> is therefore the
/// most important test here: without it an unstable algorithm would be green.</para>
///
/// <para>Quicksort would be faster but is unstable and quadratic on sorted input — the wrong surprise
/// for a standard library. Python, Java, C++ (`stable_sort`) and Go (`sort.Stable`) make the same
/// choice.</para>
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

        // 1 when sorted ascending
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

    // ------------------------------------------------------------------ base cases

    [Theory]
    [InlineData("[]")]
    [InlineData("[1]")]
    [InlineData("[2, 1]")]
    [InlineData("[5, 1, 4, 1, 9, 2, 6]")]
    [InlineData("[1, 2, 3, 4, 5]")]          // already sorted
    [InlineData("[5, 4, 3, 2, 1]")]          // exactly the wrong way round
    [InlineData("[7, 7, 7, 7]")]             // all equal
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
        // Sorted and complete are two statements. A sort losing or duplicating elements can still be
        // sorted; the sum catches both.
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
        // A hundred elements in descending order: the bottom-up loop runs over the widths 1, 2, 4, …, 64,
        // 128. Short lists never exercise the outer rounds.
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

    // ------------------------------------------------------------------ stability

    /// <summary>
    /// The most important test in this file.
    ///
    /// <para>Equal elements keep their input order. That cannot be read off a sorted list of numbers,
    /// which is why the entries here carry a mark that does not enter the comparison.</para>
    ///
    /// <para>Without this test an unstable algorithm would be green, and the whole reason for a merge
    /// sort rather than a quicksort would be void.</para>
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

                // Expected: 1/2, 1/4, 2/1, 2/3, 2/5 — ascending marks within each key, so the original
                // order.
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
        // A 'less' that never yields 'true' makes all elements equivalent. A stable algorithm then has to
        // leave the input order unchanged — and above all must not run into an infinite loop.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let xs = ausZahlen([3, 1, 2]);
                sortListBy(xs, (a: int, b: int) => false);
                if (xs.get(0) == 3 && xs.get(1) == 1 && xs.get(2) == 2) { return 1; }
                return 0;
            }
            """));

    /// <summary>
    /// A lambda with a TYPE PARAMETER as a parameter type, inside a generic function.
    ///
    /// <para>That is exactly what <c>sortList</c> is: it passes <c>(a: T, b: T) =&gt; a.compare(b) &lt; 0</c>
    /// on to <c>sortListBy</c>. The lowering aborted with "type parameter 'T' is not supported" — a lambda
    /// lowerer was always given <c>NoSubstitution</c>, even inside a monomorphized instance.</para>
    ///
    /// <para>A lambda is no generic context of its own; it inherits the one of its body. That it is lowered
    /// as a separate function is an implementation decision and must not change the types.</para>
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
