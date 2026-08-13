using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Interfaces and vtable dispatch, over the whole pipeline: source, sema, IR, bytecode, execution.
///
/// <para>The core of every test here is the same: THE SAME CALL SITE, DIFFERENT IMPLEMENTATIONS. A test
/// knowing only a single implementing type would stay green even if the dispatch bound statically to
/// the first function it found.</para>
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

    /// <summary>Two classes, one interface, one call site.</summary>
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
        // The core test. Were the dispatch static, the same value would come out twice.
        Assert.Equal(3 + 700, Run(TwoShapes + """

            fn main(): int {
                return measure(Small { n = 3 }) + measure(Big { n = 7 });
            }
            """));
    }

    [Fact]
    public void Dispatch_follows_the_value_not_the_declared_type()
    {
        // The same variable, filled in turn with two concrete types.
        //
        // The assignment goes through factories rather than directly through 's = Small { n = 5 };': the
        // parser does not recognise a struct initializer on the right of an assignment in statement
        // context, although the specification allows it "in every value position" — the block applies to
        // the START of an ExprStmt only.
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
        // An own member comes before an interface default. The resolution is decided in the lowering, and
        // this checks that it falls the right way round.
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
        // The subtle case: 'greet' is inherited and calls 'base', and 'base' has to hit the implementation
        // of the CONCRETE type rather than just any. Were 'this' statically bound in a default method, the
        // same number would come out twice.
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
        // The fat pointer carries the same reference: a mutation through the interface has to arrive at the
        // original. Had the mkiface copied, the value would stay at 1.
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
        // Two coercion sites that are not the function call: a field of interface type and a
        // '?Interface'. For the optional the order is decisive — mkiface first, then optsome; the other
        // way round a bare class reference would lie in the optional.
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
        // Enums are reference values with a tag; they may stand behind an interface just as well.
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
        // A regression found while building the interfaces and older than them: the decoder skipped inline
        // encoded types through an else-if chain that silently treated every unnamed tag as a scalar. A
        // '?Enum' therefore shifted the stream, and the fault reported itself many bytes later as
        // "unknown opcode 0x00". No test touched it, because no example and no case used a '?Enum'.
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
        // The reader's stack balance check catches a wrong effect of callvirt, but only when a void slot
        // occurs at all.
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
