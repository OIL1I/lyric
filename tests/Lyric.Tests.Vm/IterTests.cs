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
/// `std.iter` — Adapter und Terminatoren (M8b/S3).
///
/// <para><b>Alles in Lyric geschrieben.</b> Kein einziger Adapter ist nativ; die Bibliothek
/// benutzt nur, was die Sprache selbst kann — Generics, Closures, Interfaces. Dass das geht, ist
/// die eigentliche Aussage dieses Slice.</para>
///
/// <para><b>Faulheit ist die zentrale Zusicherung</b>, und sie lässt sich nicht am Ergebnis
/// ablesen: `take(map(…), 2)` liefert dasselbe, ob `map` zwei oder zweitausend Aufrufe gemacht
/// hat. Der Test `Adapters_are_lazy` zählt deshalb die Aufrufe mit einem Seiteneffekt — ohne ihn
/// wäre ein eifriger Adapter grün.</para>
///
/// <para>Nicht enthalten: `enumerate` und `zip`. Beide brauchen ein Tupel als Typargument eines
/// generischen Interfaces (`Iterator&lt;(int, T)&gt;`), und das ist nicht lowerbar.</para>
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
        // Kein panic: "nimm minus drei" ist keine gebrochene Zusage, sondern eine leere Auswahl.
        Assert.Equal(0, Run(Head + "fn main(): int { return sum(take(eins_bis_fuenf(), -3)); }"));

    [Fact]
    public void Skipping_past_the_end_yields_nothing() =>
        Assert.Equal(0, Run(Head + "fn main(): int { return sum(skip(eins_bis_fuenf(), 99)); }"));

    [Fact]
    public void TakeWhile_stops_at_the_first_failure_not_at_every_one() =>
        // Der Unterschied zu 'filter', und der ganze Zweck: nach dem ersten 'false' ist Schluss,
        // auch wenn später wieder 'true' käme. Hier: 1, 2 kommen, die 4 nicht mehr.
        Assert.Equal(3, Run(Head + """
            fn main(): int {
                return sum(takeWhile(eins_bis_fuenf(), (n: int) => n != 3));
            }
            """));

    /// <summary>
    /// <b>Faul, nicht eifrig</b> — die Zusage, die man am Ergebnis nicht sehen kann.
    ///
    /// <para>`take(map(…), 2)` liefert dasselbe, ob `map` zwei oder alle fünf Aufrufe gemacht hat.
    /// Dieser Test zählt sie deshalb mit einem Seiteneffekt in der Closure. Ohne ihn wäre ein
    /// eifriger Adapter grün — und der Grund, warum es Iteratoren statt Arrays gibt, dahin.</para>
    /// </summary>
    [Fact]
    public void Adapters_are_lazy() =>
        // Der Zaehler ist eine Klasse und kein Modul-'var' (Globals sind unveraenderlich), und
        // die Closure ist ein AUSDRUCK und kein Block: ein Block-Lambda liefert seinen
        // Rueckgabetyp nicht an die Inferenz (LYR-SEM0060 an 'U' von 'map').
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
        // Die übliche Konvention, und die einzige, mit der
        // 'all(a) && all(b) == all(chain(a, b))' gilt.
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

    // ------------------------------------------------------------------ zusammen

    [Fact]
    public void A_full_chain_runs_without_explicit_type_arguments() =>
        // Wofür die Konformanz-Inferenz gebaut wurde: kein '<int>' an irgendeiner Stelle.
        // (1+3+5) * 10 = 90.
        Assert.Equal(90, Run(Head + """
            fn main(): int {
                let ungerade = filter(eins_bis_fuenf(), (n: int) => n % 2 == 1);
                return sum(map(ungerade, (n: int) => n * 10));
            }
            """));

    [Fact]
    public void An_adapter_over_a_generic_instance_infers_its_type() =>
        // 'ArrayIterator<string>' ist selbst eine Instanz — die Inferenz muss durch die
        // Konformanz UND die Instanz-Substitution hindurch.
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
}
