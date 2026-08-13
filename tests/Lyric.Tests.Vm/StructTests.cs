using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Structs with value semantics, over the whole pipeline.
///
/// <para>Every test here checks the same thing from a different side: WHAT DID NOT HAPPEN TO THE
/// ORIGINAL AFTER THE COPY. A test that only reads would stay green even if a struct behaved like a
/// class; the mutation is the point.</para>
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
        // The core test. Were P a class, this would be 99.
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
        // The counter-check: without it the test above could also be passed by "assignment does nothing".
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
                // Through the method rather than through a field assignment: a parameter is an immutable
                // binding, and a struct field needs a 'mut fn' anyway. The point checked stays the same —
                // the receiver is a copy of the argument.
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
        // 'shift' is a 'mut fn': it changes its receiver. The receiver is a copy, though, so the original
        // stays untouched. Exactly the difference from a class.
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
        // The case a shallow copy fails at: 'inner' is itself a value and must not be shared.
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
        // The limit of the copy: the value is copied, not the world behind it. A field of class type
        // carries a reference and that is shared; otherwise the copy would silently have made a deep copy
        // of the whole object graph.
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
        // The interface value carries a reference to the slot array. The mkiface operand is already a copy
        // at that point, so the value semantics are preserved without mkiface having to know anything
        // about it.
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
        // A value type containing itself would be infinitely large. Without this check the layout building
        // would run into an infinite loop: for a class it terminates through the pre-assigned id, for a
        // value type it does not.
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
        // The counter-check: the chain breaks at the reference. Without it the cycle detection would be
        // too sharp and would reject valid programs.
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
    /// A struct parameter may be written to; the caller sees nothing of it.
    ///
    /// <para>The second part matters more. The assignment is allowed because it has no consequences; were
    /// it to have any, the permission would be wrong. Without this promise the test would stay green even
    /// if structs were accidentally passed by reference.</para>
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

    // -------------------------------------------------------------- field defaults

    /// <summary>
    /// An initializer may omit a field that has a default.
    ///
    /// <para>This did not work, and in a way that made the compiler CRASH. The sema never visited field
    /// defaults; the lowering evaluates them at the construction site and found no type in the side
    /// table, so an ErrorType, so "ir: type not lowerable: &lt;error&gt;". Because no diagnostic was
    /// reported, 'lyric check' said "ok" beforehand.</para>
    ///
    /// <para>Visible only when the initializer OMITS the field: 'K { v = 9 }' never evaluates the default,
    /// 'K { }' does. A default one can only use by overriding it is none, and every class and struct with
    /// defaults was affected.</para>
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
