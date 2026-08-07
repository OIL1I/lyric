using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// <c>match</c> über <b>Nicht-Enums</b> (M7/P5b): Literale, Or-Patterns, Ranges, Guards,
/// Bindungen — die Musterformen aus <c>Sprache.md</c> §6.3, die P3b nicht mitgenommen hat.
///
/// <para>Jede Form kommt <b>doppelt</b> vor: einmal treffend, einmal daneben. Ein Test, der nur
/// den Treffer prüft, bliebe auch grün, wenn jedes Muster auf alles passte — und genau so sähe
/// ein vergessener Test aus.</para>
/// </summary>
public class MatchTests
{
    private static long Run(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        var writer = new StringWriter();
        de.RenderText(writer);
        Assert.False(de.HasErrors, "source did not compile:\n" + writer);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;
    }

    private static long Classify(string scrutinee, string arms) =>
        Run($"fn main(): int {{ return match ({scrutinee}) {{ {arms} }}; }}");

    [Theory]
    [InlineData("5", 1)]   // trifft
    [InlineData("7", 0)]   // daneben
    public void A_literal_pattern_compares_the_value(string input, long expected) =>
        Assert.Equal(expected, Classify(input, "5 => 1, _ => 0,"));

    [Theory]
    [InlineData("1", 1)]
    [InlineData("5", 1)]   // letzte Alternative
    [InlineData("9", 0)]
    public void An_or_pattern_tries_every_alternative(string input, long expected) =>
        Assert.Equal(expected, Classify(input, "1 | 2 | 5 => 1, _ => 0,"));

    [Theory]
    [InlineData("0", 1)]   // untere Grenze
    [InlineData("9", 1)]   // obere Grenze, inklusiv
    [InlineData("10", 0)]
    public void An_inclusive_range_includes_both_ends(string input, long expected) =>
        Assert.Equal(expected, Classify(input, "0..=9 => 1, _ => 0,"));

    [Theory]
    [InlineData("8", 1)]
    [InlineData("9", 0)]   // obere Grenze, exklusiv — der Unterschied zu '..='
    public void An_exclusive_range_stops_before_its_end(string input, long expected) =>
        Assert.Equal(expected, Classify(input, "0..9 => 1, _ => 0,"));

    [Fact]
    public void A_range_rejects_values_below_it()
    {
        // Der zweite Vergleich allein wuerde hier passen — nur zusammen ist der Test richtig.
        Assert.Equal(0, Run("fn main(): int { let n = 0 - 1; return match (n) { 0..=9 => 1, _ => 0, }; }"));
    }

    [Theory]
    [InlineData("5", 1)]
    [InlineData("0", 0)]   // Muster passt, Guard nicht
    public void A_guard_can_reject_a_matching_pattern(string input, long expected) =>
        Assert.Equal(expected, Run(
            $"fn main(): int {{ let n = {input}; return match (n) {{ x if x > 1 => 1, _ => 0, }}; }}"));

    [Fact]
    public void A_binding_pattern_captures_the_scrutinee()
    {
        Assert.Equal(5, Classify("5", "x => x,"));
    }

    [Fact]
    public void A_guard_sees_the_binding_of_its_own_arm()
    {
        // Die Bindung muss VOR dem Guard stehen, sonst kennt 'x' niemand.
        Assert.Equal(12, Run("fn main(): int { let n = 12; return match (n) { x if x > 10 => x, _ => 0, }; }"));
    }

    [Theory]
    [InlineData("true", 1)]
    [InlineData("false", 0)]
    public void Match_works_over_bool(string input, long expected) =>
        Assert.Equal(expected, Classify(input, "true => 1, false => 0,"));

    [Theory]
    [InlineData("\"a\"", 1)]
    [InlineData("\"b\"", 0)]
    public void Match_works_over_string(string input, long expected) =>
        Assert.Equal(expected, Classify(input, "\"a\" => 1, _ => 0,"));

    [Fact]
    public void Match_works_over_char()
    {
        Assert.Equal(1, Run("fn main(): int { return match ('a') { 'a' => 1, _ => 0, }; }"));
    }

    [Fact]
    public void The_first_matching_arm_wins()
    {
        // Reihenfolge ist Semantik: beide Arme passen auf 5, genommen wird der erste.
        Assert.Equal(1, Classify("5", "0..=9 => 1, 5 => 2, _ => 0,"));
    }

    [Fact]
    public void Match_as_a_statement_needs_no_value()
    {
        Assert.Equal(7, Run("""
            fn main(): int {
                var n = 0;
                match (5) {
                    5 => n = 7,
                    _ => n = 1,
                }
                return n;
            }
            """));
    }

    [Fact]
    public void Enum_matching_still_works()
    {
        // Die Gegenprobe zu P3b: der gemeinsame Pfad darf den Enum-Fall nicht beschaedigt haben.
        Assert.Equal(2, Run("""
            enum Shape { Dot, Line(int) }

            fn main(): int {
                let s = Shape.Line(2);
                return match (s) { Dot => 0, Line(n) => n, };
            }
            """));
    }
}
