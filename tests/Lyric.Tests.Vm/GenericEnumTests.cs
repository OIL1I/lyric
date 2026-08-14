using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Generische Enums: <c>enum Opt&lt;T&gt; { Some(T), None }</c> (Sprache.md §3.4, §12).
///
/// <para><c>TypeTable.InternEnum</c> used to throw as soon as a generic enum appeared even as a
/// PARAMETER type; no variant had to be constructed. The sema already carried the form almost
/// completely; the wiring was missing.</para>
///
/// <para>The load-bearing promise is <see cref="Two_instantiations_do_not_share_an_entry"/>.
/// <c>Opt&lt;int&gt;</c> and <c>Opt&lt;string&gt;</c> need their own variant layouts, because the VM
/// knows no types at runtime. Sharing an entry would put an <c>i64</c> into a string slot — the same
/// class of hole as the conformance gap that computed silently wrong in release and showed only in
/// debug.</para>
/// </summary>
public class GenericEnumTests
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

        var report = new StringWriter();
        de.RenderText(report);
        Assert.False(de.HasErrors, "source did not compile: " + report);

        // verify: true — the IR verifier is the real witness here. A shared variant layout shows as a
        // slot type conflict long before it produces a wrong value.
        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        var lowering = new StringWriter();
        de.RenderText(lowering);
        Assert.True(ir is not null, "lowering failed: " + lowering);

        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;
    }

    private static IReadOnlyList<Diagnostic> Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de.Diagnostics;
    }

    private const string Opt = "enum Opt<T> { Some(T), None }\n\n";

    // ------------------------------------------------------------------ Konstruktion

    [Fact]
    public void A_tuple_variant_is_constructed_and_matched() =>
        Assert.Equal(7, Run(Opt + """
            fn main(): int {
                let o = Opt<int>.Some(7);
                return match (o) { Some(v) => v, None => 0 };
            }
            """));

    [Fact]
    public void A_unit_variant_carries_its_instance() =>
        Assert.Equal(9, Run(Opt + """
            fn main(): int {
                let o = Opt<int>.None;
                return match (o) { Some(v) => v, None => 9 };
            }
            """));

    /// <summary>A struct variant with written arguments. The parser had to learn that a further segment
    /// may stand behind the <c>&lt;int&gt;</c>: the arguments belong to the enum and the variant hangs off
    /// the back.</summary>
    [Fact]
    public void A_struct_variant_takes_written_type_arguments() =>
        Assert.Equal(12, Run("""
            enum Ev<T> { Hit { at: T, n: int }, Miss }

            fn main(): int {
                let e = Ev<int>.Hit { at = 4, n = 3 };
                return match (e) { Hit { at, n } => at * n, Miss => 0 };
            }
            """));

    /// <summary>
    /// And without written arguments when the context supplies them — the route that used to exist alone.
    /// It must not have been displaced by the new one.
    /// </summary>
    [Fact]
    public void The_instance_may_still_come_from_the_context() =>
        Assert.Equal(6, Run("""
            enum Ev<T> { Hit { at: T }, Miss }

            fn main(): int {
                let e: Ev<int> = Ev.Hit { at = 6 };
                return match (e) { Hit { at } => at, Miss => 0 };
            }
            """));

    /// <summary>
    /// The same for a TUPLE variant.
    ///
    /// <para>The struct form had always read the context, the tuple form had not: <c>Ev.Hit { … }</c>
    /// worked, <c>Opt.Some(7)</c> was an error. One question, two answers depending on the form of the
    /// variant. Both now go through the same resolution.</para>
    /// </summary>
    [Fact]
    public void A_tuple_variant_may_take_its_instance_from_the_context() =>
        Assert.Equal(7, Run(Opt + """
            fn main(): int {
                let o: Opt<int> = Opt.Some(7);
                return match (o) { Some(v) => v, None => 0 };
            }
            """));

    /// <summary>And the unit variant likewise.</summary>
    [Fact]
    public void A_unit_variant_may_take_its_instance_from_the_context() =>
        Assert.Equal(3, Run(Opt + """
            fn main(): int {
                let o: Opt<int> = Opt.None;
                return match (o) { Some(v) => v, None => 3 };
            }
            """));

    /// <summary>
    /// THE CONTEXT REACHES INTO AN ARGUMENT POSITION (2026-08-14). It did not: an argument was the one
    /// value position with no expected type, while a binding, a return and a field all had one, so the
    /// instance had to stand written out.
    ///
    /// <para>These two tests held that limit and are turned around here rather than deleted — what
    /// they measure is the same question, and the answer changed.</para>
    /// </summary>
    [Fact]
    public void The_context_reaches_into_an_argument_position() =>
        Assert.Equal(6, Run("""
            enum Ev<T> { Hit { at: T }, Miss }
            fn nimm(e: Ev<int>): int { return match (e) { Hit { at } => at, Miss => 0 }; }
            fn main(): int { return nimm(Ev.Hit { at = 6 }); }
            """));

    /// <summary>The same for the tuple form.</summary>
    [Fact]
    public void A_tuple_variant_takes_its_instance_from_the_parameter() =>
        Assert.Equal(5, Run(Opt + """
            fn nimm(o: Opt<int>): int { return match (o) { Some(v) => v, None => 0 }; }
            fn main(): int { return nimm(Opt.Some(5)); }
            """));

    /// <summary>
    /// Where there is still nothing to read: a generic function's parameter holds the type parameter
    /// itself, and offering <c>Opt&lt;T&gt;</c> as the expectation would fix the very instance the
    /// inference is supposed to determine from this argument.
    /// </summary>
    [Fact]
    public void An_open_parameter_type_is_no_expectation() =>
        Assert.Contains(Check(Opt + """
            fn nimm<T>(o: Opt<T>): int { return 0; }
            fn main(): int { return nimm(Opt.None); }
            """), d => d.Code == "LYR-SEM0063");

    // ------------------------------------------------------------------ the load-bearing promise

    /// <summary>
    /// TWO INSTANTIATIONS, TWO ENTRIES. The <c>int</c> value and the <c>string</c> value stand in the
    /// same program and both have to come out right.
    ///
    /// <para>Sharing a variant layout would put an <c>i64</c> into a string slot. In debug the verifier
    /// reports that; in release — what gets shipped — it would run through and give a silently wrong
    /// answer.</para>
    /// </summary>
    [Fact]
    public void Two_instantiations_do_not_share_an_entry() =>
        // 7 from the int branch, 100 only when the string branch really holds a string. Both in ONE
        // number, so neither half can be off unnoticed.
        Assert.Equal(107, Run(Opt + """
            fn main(): int {
                let a = Opt<int>.Some(7);
                let b = Opt<string>.Some("hallo");

                let zahl = match (a) { Some(v) => v, None => 0 };
                let wort = match (b) { Some(s) => if (s == "hallo") 100 else -1, None => -1 };
                return zahl + wort;
            }
            """));

    /// <summary>The same question one level deeper: <c>Opt&lt;Opt&lt;int&gt;&gt;</c>. The inner instance
    /// is needed while interning the outer one.</summary>
    [Fact]
    public void An_instance_may_be_nested() =>
        Assert.Equal(3, Run(Opt + """
            fn main(): int {
                let o = Opt<Opt<int>>.Some(Opt<int>.Some(3));
                return match (o) {
                    Some(inner) => match (inner) { Some(v) => v, None => 0 },
                    None => 0,
                };
            }
            """));

    // ------------------------------------------------------------------ recursion and generics

    /// <summary>
    /// An enum naming itself through a variant. This is the infinite-loop probe: the id has to stand in
    /// the instance registry BEFORE the variants are interned, or <c>Node(Tree&lt;T&gt;, …)</c> requests
    /// exactly the instance currently being built.
    /// </summary>
    [Fact]
    public void A_recursive_generic_enum_terminates() =>
        Assert.Equal(5, Run("""
            enum Tree<T> { Leaf, Node(T, Tree<T>, Tree<T>) }

            fn main(): int {
                let t = Tree<int>.Node(5, Tree<int>.Leaf, Tree<int>.Leaf);
                return match (t) { Node(v, l, r) => v, Leaf => 0 };
            }
            """));

    /// <summary>A generic enum in a generic function, with nested substitution: the <c>T</c> of the enum
    /// is the <c>T</c> of the function.</summary>
    [Fact]
    public void A_generic_function_over_a_generic_enum_works() =>
        Assert.Equal(7, Run(Opt + """
            fn hole<T>(o: Opt<T>, d: T): T { return match (o) { Some(v) => v, None => d }; }

            fn main(): int { return hole(Opt<int>.Some(4), 0) + hole(Opt<int>.None, 3); }
            """));

    /// <summary>As a field of a generic type: the instance arises while interning the field
    /// layouts.</summary>
    [Fact]
    public void It_works_as_a_field_of_a_generic_type() =>
        Assert.Equal(8, Run(Opt + """
            class Box<T> { v: Opt<T>, }

            fn main(): int {
                let b = Box<int> { v = Opt<int>.Some(8) };
                return match (b.v) { Some(x) => x, None => 0 };
            }
            """));

    /// <summary>A guard over a payload binding.</summary>
    [Fact]
    public void A_guard_over_a_payload_binding_works() =>
        Assert.Equal(2, Run(Opt + """
            fn main(): int {
                let o = Opt<int>.Some(1);
                return match (o) { Some(v) if v > 3 => 1, Some(v) => 2, None => 9 };
            }
            """));

    // ------------------------------------------------------------------ counter-checks

    /// <summary>A non-generic enum takes its old route unchanged. Without this test it would go unnoticed
    /// if the move to instance entries took the normal case along with it, and that is the majority of
    /// all code.</summary>
    [Fact]
    public void A_plain_enum_still_works() =>
        Assert.Equal(6, Run("""
            enum Shape { Circle(int), Tri { a: int, b: int }, Empty }

            fn main(): int {
                let c = Shape.Circle(6);
                let t = Shape.Tri { a = 1, b = 2 };
                let e = Shape.Empty;

                let x = match (c) { Circle(r) => r, Tri { a, b } => a, Empty => 0 };
                let y = match (t) { Circle(r) => 0, Tri { a, b } => a + b, Empty => 0 };
                let z = match (e) { Circle(r) => 9, Tri { a, b } => 9, Empty => 0 };
                return x + z * y;
            }
            """));

    [Fact]
    public void The_wrong_number_of_type_arguments_is_reported() =>
        Assert.Contains(Check(Opt + """
            fn main(): int { let o = Opt<int, string>.Some(1); return 0; }
            """), d => d.Code == "LYR-SEM0026");

    /// <summary>A wrong payload type is noticed at the construction rather than later in the
    /// <c>match</c>.</summary>
    [Fact]
    public void A_wrong_payload_type_is_rejected() =>
        Assert.Contains(Check(Opt + """
            fn main(): int { let o = Opt<int>.Some("nein"); return 0; }
            """), d => d.Code == "LYR-SEM0001");

    /// <summary>
    /// Without arguments and without context it stays an error, and the message names BOTH ways out.
    /// Writing the arguments is the more direct one.
    /// </summary>
    [Fact]
    public void Without_arguments_and_without_context_it_says_both_ways()
    {
        var reported = Assert.Single(Check("""
            enum Ev<T> { Hit { at: T }, Miss }
            fn main(): int { let e = Ev.Hit { at = 1 }; return 0; }
            """), d => d.Code == "LYR-SEM0026");

        Assert.Contains("write them", reported.Message, StringComparison.Ordinal);
    }
}
