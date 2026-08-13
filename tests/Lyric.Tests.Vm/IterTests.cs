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
/// `std.iter` — adapters and terminators.
///
/// <para>EVERYTHING IS WRITTEN IN LYRIC. Not a single adapter is native; the library uses only what
/// the language itself can do — generics, closures, interfaces. That this works is the actual
/// statement.</para>
///
/// <para>LAZINESS IS THE CENTRAL PROMISE, and it cannot be read off the result: `take(map(…), 2)`
/// yields the same whether `map` made two calls or two thousand. The test `Adapters_are_lazy`
/// therefore counts the calls with a side effect; without it an eager adapter would be green.</para>
///
/// <para>`enumerate` and `zip` were NOT included at first: both need a tuple as the type argument of a
/// generic interface (`Iterator&lt;(int, T)&gt;`), and `TypeTable.Resolve` knew arrays and optionals but
/// not tuples — the two functions tuples were introduced for.</para>
/// </summary>
public class IterTests
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
        import std.iter { RangeIterator, ArrayIterator, map, filter, take, skip, takeWhile, chain,
                          fold, count, sum, any, all, none, find, position, collectArray,
                          minValue, maxValue };

        fn eins_bis_fuenf(): RangeIterator { return RangeIterator { current = 1, end = 6 }; }

        """;

    // ------------------------------------------------------------------ Adapter

    [Theory]
    [InlineData("count(map(eins_bis_fuenf(), (n: int) => n * 10))", 5)]
    [InlineData("sum(map(eins_bis_fuenf(), (n: int) => n * 10))", 150)]
    [InlineData("count(filter(eins_bis_fuenf(), (n: int) => n % 2 == 0))", 2)]
    [InlineData("sum(take(eins_bis_fuenf(), 2))", 3)]
    [InlineData("sum(skip(eins_bis_fuenf(), 3))", 9)]
    [InlineData("sum(takeWhile(eins_bis_fuenf(), (n: int) => n < 3))", 3)]
    [InlineData("sum(chain(eins_bis_fuenf(), eins_bis_fuenf()))", 30)]
    public void An_adapter_transforms_the_sequence(string expression, long expected) =>
        Assert.Equal(expected, Run(Head + $"fn main(): int {{ return {expression}; }}"));

    [Fact]
    public void Take_beyond_the_end_stops_at_the_end() =>
        Assert.Equal(15, Run(Head + "fn main(): int { return sum(take(eins_bis_fuenf(), 99)); }"));

    [Fact]
    public void A_negative_take_yields_nothing() =>
        // No panic: "take minus three" is no broken promise but an empty selection.
        Assert.Equal(0, Run(Head + "fn main(): int { return sum(take(eins_bis_fuenf(), -3)); }"));

    [Fact]
    public void Skipping_past_the_end_yields_nothing() =>
        Assert.Equal(0, Run(Head + "fn main(): int { return sum(skip(eins_bis_fuenf(), 99)); }"));

    [Fact]
    public void TakeWhile_stops_at_the_first_failure_not_at_every_one() =>
        // The difference from 'filter', and the whole purpose: after the first 'false' it stops, even if
        // 'true' came again later. Here 1 and 2 arrive, the 4 no longer does.
        Assert.Equal(3, Run(Head + """
            fn main(): int {
                return sum(takeWhile(eins_bis_fuenf(), (n: int) => n != 3));
            }
            """));

    /// <summary>
    /// LAZY RATHER THAN EAGER — the promise that cannot be seen in the result.
    ///
    /// <para>`take(map(…), 2)` yields the same whether `map` made two calls or all five. This test
    /// therefore counts them with a side effect in the closure. Without it an eager adapter would be
    /// green, and the reason iterators exist rather than arrays would be gone.</para>
    /// </summary>
    [Fact]
    public void Adapters_are_lazy() =>
        // The counter is a class rather than a module 'var', because globals are immutable, and the
        // closure is an EXPRESSION rather than a block: a block lambda does not deliver its return type
        // to the inference (LYR-SEM0060 on the 'U' of 'map').
        Assert.Equal(2, Run(Head + """
            pub class Zaehler {
                stand: int = 0,
                pub mut fn zaehle(n: int): int {
                    this.stand = this.stand + 1;
                    return n;
                }
            }

            fn main(): int {
                let z = Zaehler { };
                let teuer = map(eins_bis_fuenf(), (n: int) => z.zaehle(n));
                let ergebnis = sum(take(teuer, 2));
                return z.stand;
            }
            """));

    // ------------------------------------------------------------------ Terminatoren

    [Theory]
    [InlineData("fold(eins_bis_fuenf(), 0, (a: int, n: int) => a + n)", 15)]
    [InlineData("fold(eins_bis_fuenf(), 100, (a: int, n: int) => a - n)", 85)]
    [InlineData("count(eins_bis_fuenf())", 5)]
    [InlineData("sum(eins_bis_fuenf())", 15)]
    [InlineData("if (any(eins_bis_fuenf(), (n: int) => n > 4)) 1 else 0", 1)]
    [InlineData("if (any(eins_bis_fuenf(), (n: int) => n > 9)) 1 else 0", 0)]
    [InlineData("if (all(eins_bis_fuenf(), (n: int) => n > 0)) 1 else 0", 1)]
    [InlineData("if (none(eins_bis_fuenf(), (n: int) => n > 9)) 1 else 0", 1)]
    [InlineData("find(eins_bis_fuenf(), (n: int) => n % 3 == 0) ?? -1", 3)]
    [InlineData("find(eins_bis_fuenf(), (n: int) => n > 9) ?? -1", -1)]
    [InlineData("position(eins_bis_fuenf(), (n: int) => n == 4) ?? -1", 3)]
    [InlineData("collectArray(eins_bis_fuenf()).length", 5)]
    [InlineData("minValue(eins_bis_fuenf()) ?? -1", 1)]
    [InlineData("maxValue(eins_bis_fuenf()) ?? -1", 5)]
    public void A_terminator_produces_a_value(string expression, long expected) =>
        Assert.Equal(expected, Run(Head + $"fn main(): int {{ return {expression}; }}"));

    [Fact]
    public void All_is_true_for_an_empty_sequence() =>
        // The usual convention, and the only one under which
        // 'all(a) && all(b) == all(chain(a, b))' holds.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let leer = RangeIterator { current = 0, end = 0 };
                return if (all(leer, (n: int) => false)) 1 else 0;
            }
            """));

    [Fact]
    public void MinValue_of_an_empty_sequence_is_null() =>
        Assert.Equal(-1, Run(Head + """
            fn main(): int {
                let leer = RangeIterator { current = 0, end = 0 };
                return minValue(leer) ?? -1;
            }
            """));

    // ------------------------------------------------------------------ combined

    [Fact]
    public void A_full_chain_runs_without_explicit_type_arguments() =>
        // What the conformance inference was built for: no '<int>' anywhere.
        // (1+3+5) * 10 = 90.
        Assert.Equal(90, Run(Head + """
            fn main(): int {
                let ungerade = filter(eins_bis_fuenf(), (n: int) => n % 2 == 1);
                return sum(map(ungerade, (n: int) => n * 10));
            }
            """));

    [Fact]
    public void An_adapter_over_a_generic_instance_infers_its_type() =>
        // 'ArrayIterator<string>' is itself an instance: the inference has to go through the conformance
        // AND the instance substitution.
        Assert.Equal(2, Run("""
            import std.iter { ArrayIterator, map, count };

            fn main(): int {
                let namen = ArrayIterator<string> { source = ["ada", "grace"], index = 0 };
                return count(map(namen, (s: string) => s + "!"));
            }
            """));

    [Fact]
    public void Collect_gathers_into_a_list() =>
        Assert.Equal(81, Run("""
            import std.iter { RangeIterator, map, filter };
            import std.collections { collect };

            fn main(): int {
                let ungerade = filter(RangeIterator { current = 1, end = 10 },
                                      (n: int) => n % 2 == 1);
                let quadrate = collect(map(ungerade, (n: int) => n * n));
                return quadrate.get(quadrate.length() - 1);
            }
            """));

    // ------------------------------------------------------------ enumerate and zip

    /// <summary>
    /// Numbers through, and the tuple comes back as the type argument of a generic interface.
    /// </summary>
    /// <remarks>That was the blockade: <c>TypeTable.Resolve</c> resolved arrays and optionals as a type
    /// argument but not tuples, and ran into "this type argument is not supported by this compiler
    /// version yet". The sema accepted it, the lowering did not; again the same rift.</remarks>
    [Fact]
    public void Enumerate_numbers_the_elements() =>
        Assert.Equal(3, Run("""
            import std.iter { ArrayIterator, enumerate, count };

            fn main(): int {
                let namen = ArrayIterator<string> { source = ["a", "b", "c"], index = 0 };
                return count(enumerate(namen));
            }
            """));

    [Fact]
    public void The_index_starts_at_zero_and_counts_up() =>
        // The sum of the indices 0+1+2 is 3; the count alone would not find a wrong numbering.
        Assert.Equal(3, Run("""
            import std.iter { ArrayIterator, enumerate };

            fn main(): int {
                let namen = ArrayIterator<string> { source = ["a", "b", "c"], index = 0 };
                var summe = 0;
                for (paar in enumerate(namen)) {
                    let (i, n) = paar;
                    summe = summe + i;
                }
                return summe;
            }
            """));

    [Fact]
    public void Zip_stops_with_the_shorter_side() =>
        // Three on the left, two on the right: two pairs. Without the check after the RIGHT call the
        // iterator would continue with half a pair.
        Assert.Equal(2, Run("""
            import std.iter { ArrayIterator, zip, count };

            fn main(): int {
                let a = ArrayIterator<int> { source = [1, 2, 3], index = 0 };
                let b = ArrayIterator<string> { source = ["x", "y"], index = 0 };
                return count(zip(a, b));
            }
            """));

    [Fact]
    public void Zip_stops_with_the_shorter_side_the_other_way_round() =>
        // The other direction: short on the left, long on the right. The two stopping points are different
        // lines in the adapter, and one test covers only one of them.
        Assert.Equal(2, Run("""
            import std.iter { ArrayIterator, zip, count };

            fn main(): int {
                let a = ArrayIterator<int> { source = [1, 2], index = 0 };
                let b = ArrayIterator<string> { source = ["x", "y", "z"], index = 0 };
                return count(zip(a, b));
            }
            """));

    [Fact]
    public void Zip_pairs_the_values_in_order() =>
        Assert.Equal(140, Run("""
            import std.iter { ArrayIterator, zip };

            fn main(): int {
                let a = ArrayIterator<int> { source = [1, 2, 3], index = 0 };
                let b = ArrayIterator<int> { source = [10, 20, 30], index = 0 };
                var summe = 0;
                for (paar in zip(a, b)) {
                    let (x, y) = paar;
                    summe = summe + x * y;   // 10 + 40 + 90
                }
                return summe;
            }
            """));

    [Fact]
    public void Zip_with_an_empty_side_yields_nothing() =>
        Assert.Equal(0, Run("""
            import std.iter { ArrayIterator, zip, count };

            fn main(): int {
                let a = ArrayIterator<int> { source = [1, 2], index = 0 };
                let b = ArrayIterator<int> { source = [], index = 0 };
                return count(zip(a, b));
            }
            """));
}
