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
/// `std.math`.
///
/// <para>FOUR NEW NATIVE EDGES, NO MORE: `asin`, `acos`, `atan`, `atan2`. Everything else is derivable
/// from `sqrt`, `pow` and `log` and is written in Lyric — `exp` as `pow(e, x)`, `log2` as
/// `log(x)/log(2)`, the hyperbolic functions through `exp`. Every native signature is a line in the
/// bytecode contract, and what is derivable does not belong there.</para>
///
/// <para>The tests here prefer EXACT values (`gcd`, `divFloor`, `powInt`) or invariants (`nextFloat`
/// lies in `[0,1)`) over floating-point approximations: a test comparing `exp(1.0) == 2.718…` to twelve
/// digits measures the accuracy of the host library rather than this stdlib.</para>
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
    [InlineData("gcd(0, 5)", 5)]         // zero is neutral
    [InlineData("gcd(-48, 18)", 6)]      // always non-negative
    [InlineData("lcm(4, 6)", 12)]
    [InlineData("lcm(0, 5)", 0)]
    [InlineData("powInt(2, 10) ?? -1", 1024)]
    [InlineData("powInt(2, 0) ?? -1", 1)]
    [InlineData("powInt(2, -1) ?? -1", -1)]   // a negative exponent gives null rather than 0
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
    /// <c>divFloor</c> rounds towards minus infinity, <c>/</c> towards zero.
    /// </summary>
    /// <remarks>The difference shows only for negative numbers and is exactly the reason for the
    /// function: whoever computes on a grid — tiles, time windows — needs -4 rather than -3. Without
    /// these lines an implementation simply forwarding <c>/</c> would be green.</remarks>
    [Theory]
    [InlineData("divFloor(7, 2)", 3)]
    [InlineData("divFloor(-7, 2)", -4)]
    [InlineData("divFloor(7, -2)", -4)]
    [InlineData("divFloor(-7, -2)", 3)]
    [InlineData("divFloor(-6, 2)", -3)]      // even: no rounding down needed
    [InlineData("modFloor(-7, 2)", 1)]       // the sign of b
    [InlineData("modFloor(7, -2)", -1)]
    [InlineData("modFloor(-6, 2)", 0)]
    public void Floor_division_rounds_towards_negative_infinity(string expression, long expected) =>
        Assert.Equal(expected, Run(Head + $"fn main(): int {{ return {expression}; }}"));

    // ------------------------------------------------------------------ floating point

    [Fact]
    public void NaN_is_recognised_without_a_native_call() =>
        // 'NaN' is the only value unequal to itself under IEEE 754, so 'isNaN' needs neither a native edge
        // nor bit access.
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
    [InlineData("trunc(0.0 - 2.7)", -2)]     // towards zero; floor would be -3
    [InlineData("trunc(2.7)", 2)]
    [InlineData("sign(0.0 - 5.0)", -1)]
    [InlineData("sign(0.0)", 0)]
    [InlineData("clamp(9.0, 0.0, 5.0)", 5)]
    [InlineData("log2(8.0)", 3)]
    [InlineData("log10(1000.0)", 3)]
    [InlineData("cbrt(27.0)", 3)]
    [InlineData("cbrt(0.0 - 27.0)", -3)]     // negative: pow(x, 1/3) alone would give NaN
    [InlineData("hypot(3.0, 4.0)", 5)]
    [InlineData("sinh(0.0)", 0)]
    [InlineData("cosh(0.0)", 1)]
    [InlineData("tanh(0.0)", 0)]
    public void Float_helpers_hit_their_exact_values(string expression, long expected) =>
        // Only values that come out exactly; otherwise the test measures the accuracy of the host library
        // rather than this stdlib.
        Assert.Equal(expected, Run(Head + $"fn main(): int {{ return ({expression}) as int; }}"));

    [Fact]
    public void Exp_and_the_constants_are_close_enough() =>
        // This does not come out exactly. A window is checked rather than a sequence of digits.
        Assert.Equal(1, Run(Head + """
            fn main(): int {
                let a = exp(1.0);
                let b = tau / pi;
                if (a > 2.71 && a < 2.72 && b > 1.999 && b < 2.001) { return 1; }
                return 0;
            }
            """));

    // ------------------------------------------------------------------ randomness

    [Fact]
    public void The_same_seed_gives_the_same_sequence() =>
        // The reason there is no 'newRandom()' without an argument: a time source as the default would be
        // convenient and would make every run silently irreproducible.
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
        // For xorshift 0 is a fixed point: the sequence would stay 0 forever. The seed is therefore
        // replaced silently — the one place where silence is better than a panic for a perfectly
        // plausible input.
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
        // A thousand draws. The raw value may be negative, and without taking the absolute value BEFORE
        // the modulo the result would lie below 'lo' — a fault a single draw misses with probability
        // one half.
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
        // A 'nextBool' always yielding the same value satisfies every range check and is still worthless.
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
