using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Interfaces und vtable-Dispatch (M7/P3), über die gesamte Pipeline: Quelle → Sema → IR →
/// Bytecode → Ausführung.
///
/// <para>Der Kern jedes Tests hier ist derselbe: <b>dieselbe Aufrufstelle, verschiedene
/// Implementierungen</b>. Ein Test, der nur einen einzigen implementierenden Typ kennt, würde auch
/// dann grün bleiben, wenn der Dispatch statisch an die erstbeste Funktion bände.</para>
/// </summary>
public class InterfaceTests
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

    /// <summary>Zwei Klassen, ein Interface, eine Aufrufstelle.</summary>
    private const string TwoShapes = """
        interface Sized {
            fn size(): int;
        }

        class Small :: [Sized] {
            n: int,
            fn size(): int { return this.n; }
        }

        class Big :: [Sized] {
            n: int,
            fn size(): int { return this.n * 100; }
        }

        fn measure(s: Sized): int { return s.size(); }
        """;

    [Fact]
    public void The_same_call_site_reaches_two_implementations()
    {
        // DER Test von P3. Waere der Dispatch statisch, kaeme zweimal dasselbe heraus.
        Assert.Equal(3 + 700, Run(TwoShapes + """

            fn main(): int {
                return measure(Small { n = 3 }) + measure(Big { n = 7 });
            }
            """));
    }

    [Fact]
    public void Dispatch_follows_the_value_not_the_declared_type()
    {
        // Dieselbe Variable, nacheinander mit zwei konkreten Typen belegt.
        //
        // Die Zuweisung geht ueber Fabriken statt direkt ueber 's = Small { n = 5 };': der Parser
        // erkennt einen StructInit heute nicht auf der rechten Seite einer Zuweisung im
        // Statement-Kontext, obwohl Sprache.md §6.2 ihn "in jeder Wert-Position" erlaubt — die
        // Sperre gilt dort nur dem ANFANG eines ExprStmt. Eigener Befund, nichts mit P3 zu tun.
        Assert.Equal(500 - 5, Run(TwoShapes + """

            fn small(n: int): Small { return Small { n = n }; }
            fn big(n: int): Big { return Big { n = n }; }

            fn main(): int {
                var s: Sized = big(5);
                let large = measure(s);
                s = small(5);
                return large - measure(s);
            }
            """));
    }

    [Fact]
    public void A_default_method_is_inherited_when_not_overridden()
    {
        Assert.Equal(1, Run("""
            interface Greeter {
                fn base(): int;
                fn greet(): int { return this.base() + 1; }
            }

            class Plain :: [Greeter] {
                fn base(): int { return 0; }
            }

            fn main(): int {
                let g: Greeter = Plain { };
                return g.greet();
            }
            """));
    }

    [Fact]
    public void An_override_beats_the_default()
    {
        // Sprache.md §3.5: eigenes Member vor Interface-Default. Die Aufloesung faellt im
        // Lowering — hier wird geprueft, dass sie richtig herum faellt.
        Assert.Equal(99, Run("""
            interface Greeter {
                fn base(): int;
                fn greet(): int { return this.base() + 1; }
            }

            class Custom :: [Greeter] {
                fn base(): int { return 0; }
                fn greet(): int { return 99; }
            }

            fn main(): int {
                let g: Greeter = Custom { };
                return g.greet();
            }
            """));
    }

    [Fact]
    public void This_inside_a_default_method_dispatches_virtually()
    {
        // Der subtile Fall: 'greet' ist geerbt und ruft 'base' — und 'base' muss die
        // Implementierung des KONKRETEN Typs treffen, nicht irgendeine. Waere 'this' in einer
        // Default-Methode statisch gebunden, kaeme hier zweimal dieselbe Zahl heraus.
        Assert.Equal(10 + 20, Run("""
            interface Greeter {
                fn base(): int;
                fn greet(): int { return this.base(); }
            }

            class Ten :: [Greeter] {
                fn base(): int { return 10; }
            }

            class Twenty :: [Greeter] {
                fn base(): int { return 20; }
            }

            fn sum(a: Greeter, b: Greeter): int { return a.greet() + b.greet(); }

            fn main(): int { return sum(Ten { }, Twenty { }); }
            """));
    }

    [Fact]
    public void A_mutating_method_reaches_the_underlying_object()
    {
        // Der Fat Pointer traegt dieselbe Referenz — eine Mutation ueber das Interface muss am
        // Original ankommen. Waere beim mkiface kopiert worden, bliebe der Wert bei 1.
        Assert.Equal(41, Run("""
            interface Counter {
                mut fn bump(by: int);
                fn value(): int;
            }

            class Cell :: [Counter] {
                n: int,
                mut fn bump(by: int) { this.n += by; }
                fn value(): int { return this.n; }
            }

            fn raise(c: Counter) { c.bump(40); }

            fn main(): int {
                let cell = Cell { n = 1 };
                raise(cell);
                return cell.n;
            }
            """));
    }

    [Fact]
    public void An_interface_value_survives_a_field_and_an_optional()
    {
        // Zwei Coercion-Stellen, die nicht der Funktionsaufruf sind: ein Feld vom Interface-Typ
        // und ein '?Interface'. Beim Optional ist die Reihenfolge entscheidend — erst mkiface,
        // dann optsome; andersherum laege eine nackte Klassenreferenz im Optional.
        Assert.Equal(700, Run(TwoShapes + """

            class Holder {
                item: Sized,
            }

            fn main(): int {
                let holder = Holder { item = Big { n = 7 } };
                let maybe: ?Sized = holder.item;
                return maybe!.size();
            }
            """));
    }

    [Fact]
    public void An_enum_can_implement_an_interface()
    {
        // Enums sind Referenzwerte mit Tag; sie duerfen genauso hinter einem Interface stehen.
        Assert.Equal(3 + 7, Run("""
            interface Sized {
                fn size(): int;
            }

            enum Shape :: [Sized] {
                Dot,
                Line(int);

                fn size(): int {
                    return match (this) {
                        Dot => 3,
                        Line(n) => n,
                    };
                }
            }

            fn measure(s: Sized): int { return s.size(); }

            fn main(): int {
                return measure(Shape.Dot) + measure(Shape.Line(7));
            }
            """));
    }

    [Fact]
    public void An_optional_enum_survives_the_instruction_stream()
    {
        // Regression, gefunden beim Bau der Interfaces und aelter als sie: der Dekodierer
        // uebersprang inline kodierte Typen ueber eine else-if-Kette, die jedes nicht genannte Tag
        // still als Skalar behandelte. Ein '?Enum' verschob damit den Strom, und der Fehler meldete
        // sich viele Bytes spaeter als "unknown opcode 0x00". Seit P3b vorhanden, von keinem Test
        // beruehrt — kein Beispiel und kein Fall benutzte '?Enum'.
        Assert.Equal(7, Run("""
            enum Shape { Dot, Line(int) }

            fn wrap(s: Shape): ?Shape { return s; }

            fn main(): int {
                let m = wrap(Shape.Line(7));
                return match (m!) { Dot => 0, Line(n) => n, };
            }
            """));
    }

    [Fact]
    public void Interface_values_carry_arguments_through()
    {
        Assert.Equal(6, Run("""
            interface Adder {
                fn add(a: int, b: int): int;
            }

            class Plus :: [Adder] {
                fn add(a: int, b: int): int { return a + b; }
            }

            fn main(): int {
                let x: Adder = Plus { };
                return x.add(1, 5);
            }
            """));
    }

    [Fact]
    public void A_void_returning_slot_leaves_the_stack_balanced()
    {
        // Die Stack-Bilanz-Pruefung des Readers faengt eine falsche Wirkung von callvirt — aber
        // nur, wenn ein void-Slot ueberhaupt vorkommt.
        Assert.Equal(5, Run("""
            interface Sink {
                mut fn accept(v: int);
                fn total(): int;
            }

            class Box :: [Sink] {
                n: int,
                mut fn accept(v: int) { this.n += v; }
                fn total(): int { return this.n; }
            }

            fn main(): int {
                let s: Sink = Box { n = 0 };
                s.accept(2);
                s.accept(3);
                return s.total();
            }
            """));
    }
}
