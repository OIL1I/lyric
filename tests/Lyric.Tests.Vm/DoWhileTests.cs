using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// <c>do { … } while (…)</c> — die einzige Schleife, deren Bedingung <b>hinter</b> dem Rumpf steht
/// und deshalb unerreichbar sein kann.
///
/// <para><b>Bis 2026-08-11 war <c>do { return 1; } while (true);</c> ein Compiler-Absturz.</b>
/// Rumpf, Bedingung und Ausgang wurden alle drei vorab angelegt; terminierte der Rumpf, blieben
/// zwei Bloecke ohne Praedecessoren stehen, und der Verifier lehnt das ab — es gibt keinen
/// <c>SimplifyCfg</c>-Pass in v1.</para>
///
/// <para><b>Die Falle steckt im zweiten Test.</b> STATUS hat den Fall lange als „Rumpf terminiert"
/// beschrieben, und daran laesst er sich nicht entscheiden: <c>do { if (c) { break; } return 2; }</c>
/// faellt nicht durch und erreicht den Ausgang trotzdem. „Ist der Block erreichbar" und „faellt der
/// Rumpf durch" sind zwei Fragen. Ein Fix, der nur die zweite stellt, waere mit dem ersten Test
/// gruen und hier rot.</para>
/// </summary>
public class DoWhileTests
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

        var diagnostics = new StringWriter();
        de.RenderText(diagnostics);
        Assert.False(de.HasErrors, "source did not compile: " + diagnostics);

        // verify: true — der Verifier IST hier der Test. Ein unerreichbarer Block faellt nur ihm
        // auf, und genau der war der Absturz.
        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;
    }

    /// <summary>Der gemeldete Fall: der Rumpf verlaesst die Funktion, niemand erreicht Bedingung
    /// oder Ausgang.</summary>
    [Fact]
    public void A_body_that_always_returns_leaves_no_unreachable_blocks() =>
        Assert.Equal(1, Run("""
            fn main(): int {
                do { return 1; } while (true);
            }
            """));

    /// <summary>
    /// Der Fall, an dem ein zu einfacher Fix scheitert: der Rumpf faellt <b>nicht</b> durch, und
    /// der Ausgang ist trotzdem erreichbar — ueber das <c>break</c>.
    /// </summary>
    [Fact]
    public void A_break_reaches_the_exit_even_from_a_body_that_never_falls_through() =>
        Assert.Equal(3, Run("""
            fn los(c: bool): int {
                do { if (c) { break; } return 2; } while (true);
                return 3;
            }

            fn main(): int { return los(true); }
            """));

    /// <summary>Dieselbe Funktion mit der anderen Antwort — sonst bliebe offen, ob der Rumpf
    /// ueberhaupt noch laeuft.</summary>
    [Fact]
    public void The_same_loop_still_returns_from_inside_the_body() =>
        Assert.Equal(2, Run("""
            fn los(c: bool): int {
                do { if (c) { break; } return 2; } while (true);
                return 3;
            }

            fn main(): int { return los(false); }
            """));

    /// <summary>Ein <c>continue</c> erreicht die Bedingung, auch wenn der Rumpf nicht
    /// durchfaellt.</summary>
    [Fact]
    public void A_continue_reaches_the_condition_from_a_body_that_never_falls_through() =>
        Assert.Equal(3, Run("""
            fn main(): int {
                var i = 0;
                do {
                    i = i + 1;
                    if (i < 3) { continue; }
                    return i;
                } while (i < 100);
                return -1;
            }
            """));

    /// <summary>
    /// Die Gegenprobe. Ohne sie bliebe alles oben gruen, wenn die gewoehnliche Schleife dabei
    /// kaputtgegangen waere — und das ist die Form, die in echtem Code steht.
    /// </summary>
    [Fact]
    public void An_ordinary_do_while_still_loops() =>
        Assert.Equal(5, Run("""
            fn main(): int {
                var i = 0;
                do { i = i + 1; } while (i < 5);
                return i;
            }
            """));

    /// <summary>Und die Endlosschleife mit <c>break</c> bleibt die Form, fuer die es
    /// <c>do-while</c> ueberhaupt gibt.</summary>
    [Fact]
    public void A_loop_that_only_leaves_through_break_works() =>
        Assert.Equal(4, Run("""
            fn main(): int {
                var i = 0;
                do {
                    i = i + 1;
                    if (i == 4) { break; }
                } while (true);
                return i;
            }
            """));
}
