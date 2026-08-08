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
/// `std.math` — die Erweiterung aus M8b/S6.
///
/// <para><b>Vier neue native Kanten, mehr nicht</b>: `asin`, `acos`, `atan`, `atan2`. Alles andere
/// ist aus `sqrt`, `pow` und `log` ableitbar und steht in Lyric — `exp` als `pow(e, x)`, `log2`
/// als `log(x)/log(2)`, die Hyperbelfunktionen über `exp`. Jede native Signatur ist eine Zeile im
/// Bytecode-Vertrag (ADR-013), und was ableitbar ist, gehört nicht darüber.</para>
///
/// <para>Die Tests hier prüfen bevorzugt <b>exakte</b> Werte (`gcd`, `divFloor`, `powInt`) oder
/// Invarianten (`nextFloat` liegt in `[0,1)`), nicht Fließkomma-Näherungen: ein Test, der
/// `exp(1.0) == 2.718…` auf zwölf Stellen vergleicht, misst die Genauigkeit der Host-Bibliothek
/// und nicht diese Stdlib.</para>
/// </summary>
public class MathTests
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
        import std.math { gcd, lcm, powInt, divFloor, modFloor, clampInt, signInt, absInt,
                          minInt, maxInt, isNaN, isInfinite, isFinite, exp, log2, log10, cbrt,
                          hypot, sinh, cosh, tanh, trunc, sign, clamp, newRandom, tau, pi };

        """;

    // ------------------------------------------------------------------ Ganzzahlen

    [Theory]
    [InlineData("gcd(48, 18)", 6)]
    [InlineData("gcd(17, 5)", 1)]        // teilerfremd
    [InlineData("gcd(0, 5)", 5)]         // Null ist neutral
    [InlineData("gcd(-48, 18)", 6)]      // immer nicht-negativ
    [InlineData("lcm(4, 6)", 12)]
    [InlineData("lcm(0, 5)", 0)]
    [InlineData("powInt(2, 10) ?? -1", 1024)]
    [InlineData("powInt(2, 0) ?? -1", 1)]
    [InlineData("powInt(2, -1) ?? -1", -1)]   // negativer Exponent: null, nicht 0
    [InlineData("absInt(-7)", 7)]
    [InlineData("minInt(3, 9)", 3)]
    [InlineData("maxInt(3, 9)", 9)]
    [InlineData("clampInt(9, 0, 5)", 5)]
    [InlineData("clampInt(-9, 0, 5)", 0)]
    [InlineData("clampInt(3, 0, 5)", 3)]
    [InlineData("signInt(-3)", -1)]
    [InlineData("signInt(0)", 0)]
    public void Integer_helpers_compute_exactly(string expression, long expected) =>
        Assert.Equal(expected, Run(Head + $"fn main(): int {{ return {expression}; }}"));

    /// <summary>
    /// <c>divFloor</c> rundet Richtung minus unendlich, <c>/</c> Richtung null.
    /// </summary>
    /// <remarks>Der Unterschied zeigt sich nur bei negativen Zahlen und ist genau der Grund für
    /// die Funktion: wer auf einem Raster rechnet (Kacheln, Zeitfenster), braucht -4 und nicht -3.
    /// Ohne diese Zeilen wäre eine Implementierung grün, die schlicht <c>/</c> weiterreicht.</remarks>
    [Theory]
    [InlineData("divFloor(7, 2)", 3)]
    [InlineData("divFloor(-7, 2)", -4)]
    [InlineData("divFloor(7, -2)", -4)]
    [InlineData("divFloor(-7, -2)", 3)]
    [InlineData("divFloor(-6, 2)", -3)]      // glatt: kein Abrunden nötig
    [InlineData("modFloor(-7, 2)", 1)]       // Vorzeichen von b
    [InlineData("modFloor(7, -2)", -1)]
    [InlineData("modFloor(-6, 2)", 0)]
    public void Floor_division_rounds_towards_negative_infinity(string expression, long expected) =>
        Assert.Equal(expected, Run(Head + $"fn main(): int {{ return {expression}; }}"));

    // ------------------------------------------------------------------ Fließkomma

    [Fact]
    public void NaN_is_recognised_without_a_native_call() =>
        // 'NaN' ist der einzige Wert, der sich selbst ungleich ist (IEEE 754) — deshalb braucht
        // 'isNaN' weder eine native Kante noch Bit-Zugriff.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let nan = 0.0 / 0.0;
                if (isNaN(nan) && !isNaN(1.0) && !isFinite(nan)) { return 1; }
                return 0;
            }
            """));

    [Fact]
    public void Infinity_is_recognised_and_is_not_NaN() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let inf = 1.0 / 0.0;
                if (isInfinite(inf) && !isNaN(inf) && !isFinite(inf) && isFinite(1.0)) {
                    return 1;
                }
                return 0;
            }
            """));

    [Theory]
    [InlineData("trunc(0.0 - 2.7)", -2)]     // Richtung null, floor wäre -3
    [InlineData("trunc(2.7)", 2)]
    [InlineData("sign(0.0 - 5.0)", -1)]
    [InlineData("sign(0.0)", 0)]
    [InlineData("clamp(9.0, 0.0, 5.0)", 5)]
    [InlineData("log2(8.0)", 3)]
    [InlineData("log10(1000.0)", 3)]
    [InlineData("cbrt(27.0)", 3)]
    [InlineData("cbrt(0.0 - 27.0)", -3)]     // negativ: pow(x, 1/3) allein gäbe NaN
    [InlineData("hypot(3.0, 4.0)", 5)]
    [InlineData("sinh(0.0)", 0)]
    [InlineData("cosh(0.0)", 1)]
    [InlineData("tanh(0.0)", 0)]
    public void Float_helpers_hit_their_exact_values(string expression, long expected) =>
        // Nur Werte, die exakt herauskommen — sonst misst der Test die Genauigkeit der
        // Host-Bibliothek statt dieser Stdlib.
        Assert.Equal(expected, Run(Head + $"fn main(): int {{ return ({expression}) as int; }}"));

    [Fact]
    public void Exp_and_the_constants_are_close_enough() =>
        // Hier geht es nicht exakt auf. Geprüft wird ein Fenster, nicht eine Ziffernfolge.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let a = exp(1.0);
                let b = tau / pi;
                if (a > 2.71 && a < 2.72 && b > 1.999 && b < 2.001) { return 1; }
                return 0;
            }
            """));

    // ------------------------------------------------------------------ Zufall

    [Fact]
    public void The_same_seed_gives_the_same_sequence() =>
        // Der Grund, warum es kein 'newRandom()' ohne Argument gibt: eine Zeitquelle als
        // Voreinstellung wäre bequem und machte jeden Lauf stillschweigend unreproduzierbar.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let a = newRandom(42);
                let b = newRandom(42);
                var i = 0;
                while (i < 20) {
                    if (a.nextInt() != b.nextInt()) { return 0; }
                    i = i + 1;
                }
                return 1;
            }
            """));

    [Fact]
    public void Different_seeds_diverge() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let a = newRandom(1);
                let b = newRandom(2);
                return if (a.nextInt() != b.nextInt()) 1 else 0;
            }
            """));

    [Fact]
    public void A_zero_seed_does_not_freeze_the_generator() =>
        // Bei xorshift ist 0 ein Fixpunkt: die Folge bliebe für immer 0. Der Seed wird deshalb
        // still ersetzt — die eine Stelle, an der Stillschweigen besser ist als ein panic für
        // eine völlig plausible Eingabe.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let r = newRandom(0);
                let a = r.nextInt();
                let b = r.nextInt();
                return if (a != 0 && b != 0 && a != b) 1 else 0;
            }
            """));

    [Fact]
    public void NextIntRange_stays_inside_its_bounds() =>
        // 1000 Ziehungen. Der Rohwert kann negativ sein, und ohne die Betragsbildung VOR dem
        // Modulo läge das Ergebnis unterhalb von 'lo' — ein Fehler, den ein einzelner Zug mit
        // Wahrscheinlichkeit 1/2 nicht sieht.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let r = newRandom(7);
                var i = 0;
                while (i < 1000) {
                    let v = r.nextIntRange(10, 20);
                    if (v < 10 || v >= 20) { return 0; }
                    i = i + 1;
                }
                return 1;
            }
            """));

    [Fact]
    public void An_empty_range_yields_its_lower_bound() =>
        Assert.Equal(5, Run(Head + """
            fn main(): int {
                let r = newRandom(7);
                return r.nextIntRange(5, 5);
            }
            """));

    [Fact]
    public void NextFloat_stays_in_the_unit_interval() =>
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let r = newRandom(3);
                var i = 0;
                while (i < 500) {
                    let v = r.nextFloat();
                    if (v < 0.0 || v >= 1.0) { return 0; }
                    i = i + 1;
                }
                return 1;
            }
            """));

    [Fact]
    public void NextBool_produces_both_values() =>
        // Ein 'nextBool', das immer dasselbe liefert, erfüllt jede Bereichsprüfung und ist
        // trotzdem wertlos.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let r = newRandom(11);
                var wahr = 0;
                var falsch = 0;
                var i = 0;
                while (i < 100) {
                    if (r.nextBool()) { wahr = wahr + 1; } else { falsch = falsch + 1; }
                    i = i + 1;
                }
                return if (wahr > 0 && falsch > 0) 1 else 0;
            }
            """));
}
