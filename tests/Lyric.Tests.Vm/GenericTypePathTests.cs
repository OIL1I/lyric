using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// A type path with arguments in value position: <c>Pair&lt;int&gt;.of(3)</c>.
///
/// <para>The form did not exist. The parser read <c>Pair</c> as an identifier and the <c>&lt;</c> as a
/// comparison, and then stumbled over the dot. A static factory on a generic type was therefore
/// unreachable — <c>std.collections</c> carries the evidence as a comment: <c>emptyList</c> is a free
/// function "because a static method on a generic instance is not expressible".</para>
///
/// <para>THE DETECTION COSTS NO AMBIGUITY. The <c>&lt;</c> counts as a type argument list when it
/// closes balanced and a <c>.</c> follows, and a dot after a comparison chain
/// (<c>a &lt; b &gt; .c</c>) is not a valid expression anyway. The same rule applies to
/// <c>f&lt;int&gt;()</c>.</para>
/// </summary>
public class GenericTypePathTests
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

    private const string Pair = """
        struct Pair<T> {
            a: T,
            static fn of(x: T): Pair<T> { return Pair<T> { a = x }; }
            fn first(): T { return this.a; }
        }

        """;

    // ------------------------------------------------------------------ what works now

    [Fact]
    public void A_static_factory_on_a_generic_type_is_callable() =>
        Assert.Equal(3, Run(Pair + """
            fn main(): int { return Pair<int>.of(3).first(); }
            """));

    /// <summary>The factory's return type is <c>Pair&lt;T&gt;</c> and has to arrive as
    /// <c>Pair&lt;int&gt;</c>; without a substitution a <c>T</c> would stand there.</summary>
    [Fact]
    public void The_result_carries_the_type_argument() =>
        Assert.Equal(7, Run(Pair + """
            fn main(): int {
                let p: Pair<int> = Pair<int>.of(7);
                return p.a;
            }
            """));

    /// <summary>Two instantiations are two functions. Without that, <c>int</c> and <c>bool</c> would share
    /// one body and the monomorphization would be none.</summary>
    [Fact]
    public void Two_instantiations_do_not_share_a_body() =>
        Assert.Equal(1, Run("""
            struct Box<T> {
                v: T,
                static fn of(x: T): Box<T> { return Box<T> { v = x }; }
            }

            fn main(): int {
                let a = Box<int>.of(1);
                let b = Box<bool>.of(true);
                return if (b.v) a.v else 0;
            }
            """));

    /// <summary>Two type parameters: the arguments have to be assigned in order.
    /// </summary>
    [Fact]
    public void Two_type_parameters_keep_their_order() =>
        Assert.Equal(5, Run("""
            struct Two<A, B> {
                a: A, b: B,
                static fn of(x: A, y: B): Two<A, B> { return Two<A, B> { a = x, b = y }; }
            }

            fn main(): int {
                let t = Two<int, bool>.of(5, true);
                return if (t.b) t.a else 0;
            }
            """));

    /// <summary>A nested type argument: <c>Pair&lt;Pair&lt;int&gt;&gt;</c> closes with a <c>&gt;&gt;</c>,
    /// and the lookahead has to read that as two levels.</summary>
    [Fact]
    public void A_nested_type_argument_closes_with_a_shift_token() =>
        Assert.Equal(9, Run(Pair + """
            fn main(): int {
                let inner = Pair<int>.of(9);
                let outer = Pair<Pair<int>>.of(inner);
                return outer.a.a;
            }
            """));

    /// <summary>A class rather than a struct: the same route, a different store.</summary>
    [Fact]
    public void It_works_on_a_class_too() =>
        Assert.Equal(4, Run("""
            class Holder<T> {
                v: T,
                static fn of(x: T): Holder<T> { return Holder<T> { v = x }; }
            }

            fn main(): int { return Holder<int>.of(4).v; }
            """));

    // ------------------------------------------------------------------ counter-checks

    /// <summary>
    /// The most important promise. A <c>&lt;</c> stays a comparison when no balanced <c>&gt;</c> with a
    /// following <c>.</c> stands behind it. Without this test a detection that is too greedy would be
    /// green, and it would cost no diagnostic but a wrong reading.
    /// </summary>
    [Fact]
    public void A_comparison_stays_a_comparison() =>
        Assert.Equal(1, Run("""
            fn main(): int {
                let a = 1;
                let b = 2;
                return if (a < b) 1 else 0;
            }
            """));

    /// <summary>And the nastier form: a comparison really followed by a dot. <c>(a &lt; b) == c.d</c> must
    /// not be read as a type path.</summary>
    [Fact]
    public void A_comparison_followed_by_a_member_access_stays_a_comparison() =>
        Assert.Equal(1, Run("""
            struct Flag { v: bool, }

            fn main(): int {
                let a = 1;
                let b = 2;
                let f = Flag { v = true };
                return if ((a < b) == f.v) 1 else 0;
            }
            """));

    /// <summary>The non-generic case still takes its old route: it does not need the new node.</summary>
    [Fact]
    public void A_static_method_on_a_plain_type_still_works() =>
        Assert.Equal(2, Run("""
            struct P { n: int, static fn neu(): P { return P { n = 2 }; } }
            fn main(): int { return P.neu().n; }
            """));

    /// <summary>And the generic struct initializer.</summary>
    [Fact]
    public void A_generic_struct_init_still_works() =>
        Assert.Equal(6, Run(Pair + """
            fn main(): int { return Pair<int> { a = 6 }.a; }
            """));

    // ------------------------------------------------------------------ Diagnosen

    /// <summary>
    /// Without arguments it now says so. It used to report "cannot assign 'int' to 'T'", a statement about
    /// the consequence rather than about the cause, pointing at the argument rather than at the missing
    /// type.
    /// </summary>
    [Fact]
    public void A_generic_type_without_arguments_says_so()
    {
        var reported = Assert.Single(Check(Pair + """
            fn main(): int { return Pair.of(3).a; }
            """), d => d.Code == "LYR-SEM0063");

        Assert.Contains("Pair<T>.of", reported.Message, StringComparison.Ordinal);
    }

    /// <summary>The wrong number of arguments is a message of its own.</summary>
    [Fact]
    public void The_wrong_number_of_type_arguments_is_reported() =>
        Assert.Contains(Check(Pair + """
            fn main(): int { return Pair<int, bool>.of(3).a; }
            """), d => d.Code == "LYR-SEM0026");

    /// <summary>A wrong type argument is noticed at the call rather than only in the body.</summary>
    [Fact]
    public void A_wrong_argument_type_is_rejected() =>
        Assert.Contains(Check(Pair + """
            fn main(): int { return Pair<int>.of(true).a; }
            """), d => d.Code == "LYR-SEM0001");

    /// <summary>An instance method through the type path stays rejected: it needs a receiver, and type
    /// arguments change nothing about that.</summary>
    [Fact]
    public void An_instance_method_through_a_type_path_is_still_rejected() =>
        Assert.Contains(Check(Pair + """
            fn main(): int { return Pair<int>.first(); }
            """), d => d.Code == "LYR-SEM0055");

    /// <summary>A type path alone is no value: the same message as for any type name.
    /// </summary>
    [Fact]
    public void A_type_path_alone_is_not_a_value() =>
        Assert.Contains(Check(Pair + """
            fn main(): int { let x = Pair<int>; return 0; }
            """), d => d.Code is "LYR-SEM0052" or "LYR-PAR0002");
}
