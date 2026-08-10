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
/// `std.option` und die Abbruch-Funktionen aus `std.core` (M8b/S9).
///
/// <para><b>Das Modul enthaelt keinen Typ `Option&lt;T&gt;`.</b> `?T` ist er (Sprache.md §4); ein
/// zweiter waere der Doppel-Mechanismus aus CONTRIBUTING Rule 2. Was hier geprueft wird, sind
/// Funktionen ueber dem eingebauten Typ.</para>
///
/// <para><b>Vier Namen aus `Doku.md` §22 fehlen mit Absicht</b>, und der Grund ist jedes Mal, dass
/// die Sprache sie schon hat: `unwrap` ist `!`, `unwrapOr` ist `??`, `isSome`/`isNone` sind
/// `!= null`/`== null`. Der letzte Fall ist nicht bloss Redundanz, sondern schaedlich — an
/// `!= null` haengt das Flow-Narrowing (§7), und eine Funktion schnitte es ab. `flatten` ist gar
/// nicht formulierbar: verschachtelte Optionals gibt es nicht.</para>
/// </summary>
public class OptionTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static BytecodeModule Compile(string source)
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

        var diagnostics = new StringWriter();
        de.RenderText(diagnostics);
        Assert.False(de.HasErrors, "source did not compile: " + diagnostics);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);
        return BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!));
    }

    private static long Run(string source) =>
        Interpreter.Run(Compile(source),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;

    private const string Head =
        "import std.option { map, andThen, filter, zip, contains, toArray, iter, expect };\n";

    // ------------------------------------------------------------------ map / andThen

    [Fact]
    public void Map_applies_the_function_only_when_a_value_is_present() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let voll: ?int = 8;
                let leer: ?int = null;
                let a = map(voll, (n: int) => n + 1);
                let b = map(leer, (n: int) => n + 1);
                return if ((a ?? 0) == 9 && b == null) 1 else 0;
            }
            """));

    /// <summary>
    /// Der Test, der `map` von `andThen` unterscheidet: die Funktion darf selbst scheitern, und
    /// das Ergebnis bleibt ein einfaches `?U`.
    ///
    /// <para>Beide Faelle sind noetig. Nur der erfolgreiche liefe auch mit einem `map`, dessen
    /// Ergebnis niemand auspackt; erst das `null` aus `f` zeigt, dass hier nichts verschachtelt
    /// wird.</para>
    /// </summary>
    [Fact]
    public void AndThen_lets_the_function_fail_without_nesting() =>
        Assert.Equal(1, Run(Head + """
            fn halb(n: int): ?int {
                if (n % 2 == 0) { return n / 2; }
                return null;
            }

            fn main(): int {
                let acht: ?int = 8;
                let sieben: ?int = 7;
                let a = andThen(acht, (n: int) => halb(n));
                let b = andThen(sieben, (n: int) => halb(n));
                let c = andThen(null, (n: int) => halb(n));
                return if ((a ?? 0) == 4 && b == null && c == null) 1 else 0;
            }
            """));

    // ------------------------------------------------------------------ filter / zip / contains

    [Fact]
    public void Filter_drops_a_value_the_predicate_rejects() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let v: ?int = 8;
                let behalten = filter(v, (n: int) => n > 5);
                let verworfen = filter(v, (n: int) => n > 50);
                return if ((behalten ?? 0) == 8 && verworfen == null) 1 else 0;
            }
            """));

    /// <summary>
    /// `zip` braucht <b>beide</b> Ausfallrichtungen. Mit nur einem leeren Argument bliebe der Test
    /// gruen, wenn die Funktion nur die linke Seite prueft — dieselbe Lehre wie bei `zip` in
    /// `std.iter`, das aus genau diesem Grund zwei Tests hat.
    /// </summary>
    [Fact]
    public void Zip_needs_both_sides_present() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let a: ?int = 3;
                let b: ?int = 4;
                let leer: ?int = null;

                let beide = zip(a, b);
                var summe = 0;
                if (beide != null) {
                    let (x, y) = beide;
                    summe = x + y;
                }

                let linksLeer = zip(leer, b);
                let rechtsLeer = zip(a, leer);
                return if (summe == 7 && linksLeer == null && rechtsLeer == null) 1 else 0;
            }
            """));

    /// <summary>
    /// Ein Tupel als Nutzlast eines Optionals — `?(T, U)`. Bis 2026-08-09 loeste
    /// `TypeTable.Resolve` Tupel als Typargument nicht auf; dass es hier traegt, ist kein
    /// Selbstverstaendnis.
    /// </summary>
    [Fact]
    public void Zip_carries_a_tuple_of_two_different_types() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let n: ?int = 42;
                let s: ?string = "hi";
                let p = zip(n, s);
                if (p == null) { return 0; }
                let (zahl, text) = p;
                return if (zahl == 42 && text == "hi") 1 else 0;
            }
            """));

    [Fact]
    public void Contains_is_false_for_an_empty_optional() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let v: ?int = 8;
                let leer: ?int = null;
                return if (contains(v, 8) && !contains(v, 9) && !contains(leer, 8)) 1 else 0;
            }
            """));

    // ------------------------------------------------------------------ Uebergaenge

    [Fact]
    public void ToArray_yields_zero_or_one_element() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let v: ?int = 8;
                let leer: ?int = null;
                let a = toArray(v);
                let b = toArray(leer);
                return if (a.length == 1 && a[0] == 8 && b.length == 0) 1 else 0;
            }
            """));

    /// <summary>
    /// Der Iterator liefert genau einen Wert und danach nichts mehr.
    ///
    /// <para>Die Schleife zaehlt <b>mit</b>, statt nur die Summe zu pruefen: ohne das `done`-Flag
    /// liefe `next()` endlos denselben Wert, und eine Summe allein saehe das nicht — sie liefe
    /// einfach nie fertig. Der Zaehler macht aus einer Endlosschleife einen Fehlschlag.</para>
    /// </summary>
    [Fact]
    public void Iterating_an_optional_yields_it_exactly_once() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let v: ?int = 8;
                var summe = 0;
                var runden = 0;
                for (x in iter(v)) {
                    summe = summe + x;
                    runden = runden + 1;
                    if (runden > 5) { return 0; }
                }
                return if (summe == 8 && runden == 1) 1 else 0;
            }
            """));

    [Fact]
    public void Iterating_an_empty_optional_yields_nothing() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let leer: ?int = null;
                var runden = 0;
                for (x in iter(leer)) { runden = runden + 1; }
                return if (runden == 0) 1 else 0;
            }
            """));

    /// <summary>
    /// Die eigentliche Begruendung fuer `iter`: die Adapter aus `std.iter` gelten unveraendert,
    /// ohne dass `std.iter` etwas von `?T` wissen muesste.
    /// </summary>
    [Fact]
    public void Std_iter_adapters_work_on_an_optional() =>
        Assert.Equal(1, Run("""
            import std.option;
            import std.iter;

            fn main(): int {
                let v: ?int = 8;
                let leer: ?int = null;
                return if (iter.sum(iter.map(option.iter(v), (n: int) => n * 2)) == 16
                        && iter.sum(iter.map(option.iter(leer), (n: int) => n * 2)) == 0) 1 else 0;
            }
            """));

    // ------------------------------------------------------------------ expect

    [Fact]
    public void Expect_returns_the_value_when_there_is_one() =>
        Assert.Equal(8, Run(Head + """
            fn main(): int {
                let v: ?int = 8;
                return expect(v, "v fehlt");
            }
            """));

    /// <summary>
    /// Der Grund, warum `expect` neben `!` existiert: die Meldung nennt den Wert.
    ///
    /// <para>`LYR-VM0007` sagt nur „force-unwrapped a '?T' that had no value" — also DASS etwas
    /// fehlte, nie WAS. Der Test prueft deshalb den <b>Text</b>; ohne ihn waere ein `expect`,
    /// das die Meldung verwirft, gruen und damit sinnlos.</para>
    /// </summary>
    [Fact]
    public void Expect_panics_with_the_given_message()
    {
        var module = Compile(Head + """
            fn main(): int {
                let leer: ?int = null;
                return expect(leer, "der Konfigurationspfad fehlt");
            }
            """);

        var panic = Assert.Throws<LyricPanic>(() => Interpreter.Run(module,
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)));

        Assert.Contains("der Konfigurationspfad fehlt", panic.Message);
    }

    // ------------------------------------------------------------------ std.core

    [Fact]
    public void Assert_lets_a_true_condition_pass() =>
        Assert.Equal(1, Run("""
            import std.core { assert };
            fn main(): int {
                assert(true, "haelt");
                return 1;
            }
            """));

    [Fact]
    public void Assert_panics_on_a_false_condition()
    {
        var module = Compile("""
            import std.core { assert };
            fn main(): int {
                assert(1 > 2, "eins ist nicht groesser als zwei");
                return 1;
            }
            """);

        var panic = Assert.Throws<LyricPanic>(() => Interpreter.Run(module,
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)));

        Assert.Contains("eins ist nicht groesser als zwei", panic.Message);
    }

    /// <summary>
    /// `todo` und `unreachable` unterscheiden sich <b>nur</b> in ihrer Aussage — und genau die
    /// steht im Text. Ein Test, der bloss „panict" prueft, liesse zu, dass beide dasselbe sagen,
    /// und dann waere eine der beiden ueberfluessig.
    /// </summary>
    [Theory]
    [InlineData("todo", "not implemented: der Rest")]
    [InlineData("unreachable", "unreachable: das Enum ist erschoepft")]
    public void Todo_and_unreachable_name_which_kind_of_gap_they_are(string name, string expected)
    {
        var argument = name == "todo" ? "der Rest" : "das Enum ist erschoepft";
        var module = Compile($$"""
            import std.core { {{name}} };
            fn main(): int {
                {{name}}("{{argument}}");
                return 1;
            }
            """);

        var panic = Assert.Throws<LyricPanic>(() => Interpreter.Run(module,
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)));

        Assert.Contains(expected, panic.Message);
    }

    // ------------------------------------------------------------------ Exception

    /// <summary>
    /// `Exception` liegt in `std.core` und nicht in einem `std.error`: das Modul haette diese eine
    /// Klasse enthalten. Die beiden Fehler, die es sonst noch tragen sollte
    /// (`NullDereferenceError`, `CoroutineEndedError`), bleiben Panics — `throw` ist fuer
    /// Domain-Fehler, `panic` fuer Programmierfehler (Doku §17.1).
    /// </summary>
    [Fact]
    public void An_exception_is_throwable_and_carries_its_message() =>
        Assert.Equal(1, Run("""
            import std.core { Exception };

            fn wirf(): int throws Exception {
                throw Exception { text = "kaputt" };
            }

            fn main(): int {
                try {
                    let x = wirf();
                    return 0;
                } catch (e: Exception) {
                    return if (e.message() == "kaputt") 1 else 0;
                }
            }
            """));

    /// <summary>
    /// Ueber die `Throwable`-Kante gefangen, nicht ueber den konkreten Typ — der Fall, fuer den
    /// die Konformanz ueberhaupt da ist. Ohne diesen Test bliebe unklar, ob `:: [Throwable]` an
    /// `Exception` mehr ist als Zierde.
    /// </summary>
    [Fact]
    public void An_exception_is_caught_by_an_untyped_catch() =>
        Assert.Equal(1, Run("""
            import std.core { Exception };

            fn wirf(): int throws Exception {
                throw Exception { text = "ueber Throwable" };
            }

            fn main(): int {
                try {
                    let x = wirf();
                    return 0;
                } catch (e) {
                    return if (e.message() == "ueber Throwable") 1 else 0;
                }
            }
            """));
}
