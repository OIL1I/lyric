using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Structs mit Wert-Semantik (M7/P4), über die gesamte Pipeline.
///
/// <para>Jeder Test hier prüft dasselbe von einer anderen Seite: <b>was nach der Kopie am
/// Original nicht passiert ist</b>. Ein Test, der nur liest, bliebe auch dann grün, wenn ein
/// struct sich wie eine class verhielte — die Mutation ist der Punkt.</para>
/// </summary>
public class StructTests
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

    private const string Point = """
        struct P {
            x: int,
            y: int,

            fn sum(): int { return this.x + this.y; }

            mut fn shift(by: int) {
                this.x += by;
                this.y += by;
            }
        }
        """;

    [Fact]
    public void Assignment_copies()
    {
        // DER Test von P4. Waere P eine class, kaeme 99 heraus.
        Assert.Equal(1, Run(Point + """

            fn main(): int {
                let a = P { x = 1, y = 0 };
                var b = a;
                b.x = 99;
                return a.x;
            }
            """));
    }

    [Fact]
    public void The_copy_is_the_one_that_changed()
    {
        // Die Gegenprobe: ohne sie liesse sich der Test oben auch mit "Zuweisung tut nichts"
        // bestehen.
        Assert.Equal(99, Run(Point + """

            fn main(): int {
                let a = P { x = 1, y = 0 };
                var b = a;
                b.x = 99;
                return b.x;
            }
            """));
    }

    [Fact]
    public void A_parameter_is_a_copy()
    {
        Assert.Equal(1, Run(Point + """

            fn wreck(p: P): int {
                // Ueber die Methode, nicht ueber eine Feldzuweisung: ein Parameter ist eine
                // immutable Bindung, und Sprache.md §6.4 verlangt fuer ein struct-Feld ohnehin
                // eine 'mut fn'. Der geprueft Punkt bleibt derselbe - der Empfaenger ist eine
                // Kopie des Arguments.
                p.shift(98);
                return p.x;
            }

            fn main(): int {
                let a = P { x = 1, y = 0 };
                wreck(a);
                return a.x;
            }
            """));
    }

    [Fact]
    public void A_mutating_method_only_mutates_its_own_copy()
    {
        // 'shift' ist 'mut fn' — es aendert seinen Empfaenger. Der Empfaenger ist aber eine
        // Kopie, also bleibt das Original in Ruhe. Genau der Unterschied zu P3s Klassen.
        Assert.Equal(1, Run(Point + """

            fn move(p: P): int {
                p.shift(10);
                return p.x;
            }

            fn main(): int {
                let a = P { x = 1, y = 0 };
                move(a);
                return a.x;
            }
            """));
    }

    [Fact]
    public void A_returned_struct_is_independent_of_its_source()
    {
        Assert.Equal(1, Run(Point + """

            fn give(p: P): P { return p; }

            fn main(): int {
                let a = P { x = 1, y = 0 };
                var b = give(a);
                b.x = 99;
                return a.x;
            }
            """));
    }

    [Fact]
    public void A_nested_struct_is_copied_through()
    {
        // Der Fall, an dem eine flache Kopie scheitert: 'inner' ist selbst ein Wert und darf
        // nicht geteilt werden.
        Assert.Equal(1, Run(Point + """

            struct Line {
                a: P,
                b: P,
            }

            fn main(): int {
                let one = P { x = 1, y = 0 };
                let line = Line { a = one, b = one };
                var moved = line;
                moved.a.x = 99;
                return line.a.x;
            }
            """));
    }

    [Fact]
    public void A_struct_field_read_yields_an_independent_value()
    {
        Assert.Equal(1, Run(Point + """

            struct Line {
                a: P,
                b: P,
            }

            fn main(): int {
                let line = Line { a = P { x = 1, y = 0 }, b = P { x = 0, y = 0 } };
                var taken = line.a;
                taken.x = 99;
                return line.a.x;
            }
            """));
    }

    [Fact]
    public void A_class_inside_a_struct_stays_shared()
    {
        // Die Grenze der Kopie: kopiert wird der Wert, nicht die Welt dahinter (Sprache.md §3.2).
        // Ein Feld vom Typ 'class' traegt eine Referenz, und die wird geteilt — waere das anders,
        // haette die Kopie stillschweigend eine tiefe Kopie des ganzen Objektgraphen gemacht.
        Assert.Equal(99, Run("""
            class Cell { n: int }

            struct Holder { cell: Cell }

            fn main(): int {
                let shared = Cell { n = 1 };
                let a = Holder { cell = shared };
                var b = a;
                b.cell.n = 99;
                return shared.n;
            }
            """));
    }

    [Fact]
    public void Two_copies_of_the_same_source_are_independent()
    {
        Assert.Equal(3, Run(Point + """

            fn main(): int {
                let a = P { x = 1, y = 0 };
                var b = a;
                var c = a;
                b.x = 10;
                c.x = 20;
                return a.x + b.x / 10 + c.x / 20;
            }
            """));
    }

    [Fact]
    public void A_static_factory_returns_a_fresh_value_each_time()
    {
        Assert.Equal(1, Run("""
            struct P {
                x: int,
                static fn one(): P { return P { x = 1 }; }
            }

            fn main(): int {
                var a = P.one();
                var b = P.one();
                b.x = 99;
                return a.x;
            }
            """));
    }

    [Fact]
    public void A_struct_can_implement_an_interface()
    {
        // P3 trifft P4: der Interface-Wert traegt eine Referenz auf das Slot-Array. Der
        // mkiface-Operand ist zu diesem Zeitpunkt bereits eine Kopie, also bleibt die
        // Wert-Semantik erhalten, ohne dass mkiface etwas davon wissen muss.
        Assert.Equal(7, Run("""
            interface Sized { fn size(): int; }

            struct Box :: [Sized] {
                n: int,
                fn size(): int { return this.n; }
            }

            fn measure(s: Sized): int { return s.size(); }

            fn main(): int { return measure(Box { n = 7 }); }
            """));
    }

    [Fact]
    public void A_recursive_struct_is_rejected_before_lowering()
    {
        // Ein Wert-Typ, der sich selbst enthaelt, waere unendlich gross. Ohne diese Pruefung
        // liefe das Layout-Bauen in eine Endlosschleife — bei einer class terminiert es ueber die
        // vorab vergebene Id, bei einem Wert-Typ nicht.
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "struct Node { next: Node }\nfn main(): int { return 0; }");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);

        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0056");
    }

    [Fact]
    public void A_struct_holding_a_class_that_holds_itself_is_fine()
    {
        // Die Gegenprobe: die Kette bricht an der Referenz. Ohne sie waere die Zyklus-Erkennung
        // zu scharf und lehnte gueltige Programme ab.
        Assert.Equal(0, Run("""
            class Node { next: ?Node }

            struct Head { first: ?Node }

            fn main(): int {
                let h = Head { first = null };
                return if (h.first == null) 0 else 1;
            }
            """));
    }

    /// <summary>
    /// Ein Struct-Parameter darf beschrieben werden (ADR-023) — der Aufrufer sieht davon nichts.
    ///
    /// <para>Der wichtigere Teil ist der zweite. ADR-023 erlaubt die Zuweisung, weil sie folgenlos
    /// ist; waere sie es nicht, waere die Erlaubnis falsch. Ohne diese Zusicherung bliebe der Test
    /// auch dann gruen, wenn Structs versehentlich per Referenz uebergeben wuerden.</para>
    /// </summary>
    [Fact]
    public void A_struct_parameter_keeps_value_semantics()
    {
        Assert.Equal(1, Run("""
            struct V { x: int, }
            fn f(v: V): int { v.x = 99; return v.x; }
            fn main(): int {
                let a = V { x = 1 };
                f(a);
                return a.x;
            }
            """));
    }

    // -------------------------------------------------------------- Feld-Defaults

    /// <summary>
    /// Ein Initialisierer darf ein Feld weglassen, das einen Default hat.
    ///
    /// <para><b>Das ging nicht — und zwar so, dass der Compiler ABSTUERZTE.</b> Die Sema hat
    /// Feld-Defaults nie besucht; das Lowering wertet sie an der Konstruktionsstelle aus und fand
    /// in der Seitentabelle keinen Typ, also ErrorType, also „ir: type not lowerable:
    /// &lt;error&gt;". Weil keine Diagnose gemeldet war, sagte 'lyric check' vorher „ok".</para>
    ///
    /// <para>Sichtbar nur, wenn der Initialisierer das Feld WEGLAESST: 'K { v = 9 }' wertet den
    /// Default nie aus, 'K { }' schon. Ein Default, den man nur benutzen kann, indem man ihn
    /// ueberschreibt, ist keiner — betroffen war jede Klasse und jeder Struct mit Defaults.</para>
    /// </summary>
    [Fact]
    public void An_initializer_may_omit_a_field_that_has_a_default() =>
        Assert.Equal(8, Run("""
            class K { a: int = 5, b: int = 3, }
            fn main(): int { let k = K { }; return k.a + k.b; }
            """));

    [Fact]
    public void A_default_may_be_overridden() =>
        Assert.Equal(12, Run("""
            class K { a: int = 5, b: int = 3, }
            fn main(): int { let k = K { a = 9 }; return k.a + k.b; }
            """));

    [Fact]
    public void A_struct_default_works_too() =>
        Assert.Equal(7, Run("""
            struct V { n: int = 7, }
            fn main(): int { let v = V { }; return v.n; }
            """));
}
