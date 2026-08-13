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
/// Explicit type arguments at a call site: `f&lt;int&gt;()`.
///
/// <para>WHY THEY ARE NEEDED: until then generics could be instantiated through argument inference
/// only. A function without a parameter of type `T` — a factory such as `empty&lt;T&gt;(): List&lt;T&gt;`
/// — was therefore not callable at all.</para>
///
/// <para>THE RISKY PART IS THE DISAMBIGUATION rather than the semantics: `f&lt;a&gt;(b)` looks like the
/// comparison chain `(f &lt; a) &gt; (b)`. The parser decides with a pure token scan — it counts
/// brackets and checks whether a `(` follows the `&gt;`. No speculative parsing, because a discarded
/// guess would leave diagnostics behind.</para>
///
/// <para>Every comparison case that looks similar therefore stands here. A test checking only the type
/// arguments would stay green even if the scan swallowed every `&lt;`.</para>
/// </summary>
public class ExplicitTypeArgumentTests
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

    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    // ------------------------------------------------------------------ what works now

    [Fact]
    public void A_call_can_name_its_type_argument() =>
        Assert.Equal(5, Run("""
            fn id<T>(x: T): T { return x; }
            fn main(): int { return id<int>(5); }
            """));

    [Fact]
    public void A_factory_without_arguments_becomes_callable() =>
        // The case. Without explicit type arguments the inference has nothing to draw T from, and the
        // function is simply not callable.
        Assert.Equal(0, Run("""
            class Buf<T> { data: T[], count: int, }
            fn empty<T>(): Buf<T> { return Buf<T> { data = [], count = 0 }; }
            fn main(): int { let b = empty<int>(); return b.count; }
            """));

    [Fact]
    public void A_generic_function_can_return_a_generic_type() =>
        // This did not work WITH inference either: 'LowerSubstituted' knew '?T' and 'T[]' but not
        // 'Box<T>', so the return type was resolved without a substitution.
        Assert.Equal(7, Run("""
            class Box<T> { v: T, }
            fn make<T>(x: T): Box<T> { return Box<T> { v = x }; }
            fn main(): int { let b = make(7); return b.v; }
            """));

    [Fact]
    public void A_growing_buffer_works_end_to_end() =>
        // The basis for 'List<T>': growing works without a 'newArray<T>(n)'. 'data + data' doubles, and
        // whatever lies beyond 'count' is never read, so its content does not matter.
        Assert.Equal(63, Run("""
            class Buf<T> {
                data: T[],
                count: int,

                pub mut fn push(v: T): void {
                    if (this.count >= this.data.length) {
                        if (this.data.length == 0) { this.data = [v]; }
                        else { this.data = this.data + this.data; }
                    }
                    this.data[this.count] = v;
                    this.count = this.count + 1;
                }

                pub fn at(i: int): T { return this.data[i]; }
            }

            fn empty<T>(): Buf<T> { return Buf<T> { data = [], count = 0 }; }

            fn main(): int {
                let b = empty<int>();
                b.push(10);
                b.push(20);
                b.push(30);
                return b.at(0) + b.at(1) + b.at(2) + b.count;
            }
            """));

    // ------------------------------------------------------------------ disambiguation

    [Fact]
    public void A_less_than_comparison_is_still_a_comparison() =>
        Assert.Equal(1, Run("fn main(): int { if (1 < 2) { return 1; } return 0; }"));

    [Fact]
    public void A_chain_that_looks_like_type_arguments_stays_a_comparison() =>
        // 'a < b > (c)' — exactly the shape the scan has to keep apart from type arguments. Here the
        // call character is missing: the callee is no generic name, and even if the scan struck, the
        // result would be a type error rather than a silent misunderstanding.
        Assert.Equal(1, Run("""
            fn main(): int {
                let a = 1;
                let b = 5;
                let c = 0;
                if (a < b) { if (b > c) { return 1; } }
                return 0;
            }
            """));

    [Fact]
    public void Arithmetic_after_a_comparison_is_not_swallowed() =>
        Assert.Equal(1, Run("fn main(): int { let n = 3; if (n < 2 + 5) { return 1; } return 0; }"));

    // ------------------------------------------------------------------ what gets rejected

    [Fact]
    public void The_wrong_number_of_type_arguments_is_reported() =>
        Assert.Contains(Check("""
            fn id<T>(x: T): T { return x; }
            fn main(): int { return id<int, bool>(5); }
            """).Diagnostics, d => d.Code == "LYR-SEM0026");

    [Fact]
    public void An_explicit_type_argument_beats_inference()
    {
        // What is written wins: 'id<int>("x")' is a type error and does NOT silently become
        // 'id<string>'. Without this order the explicit form would have no effect.
        var de = Check("""
            fn id<T>(x: T): T { return x; }
            fn main(): int { let s = id<int>("x"); return 0; }
            """);

        Assert.Contains(de.Diagnostics, d => d.Code is "LYR-SEM0001" or "LYR-SEM0014");
    }

    [Fact]
    public void A_constraint_still_applies_to_a_written_type_argument() =>
        // Otherwise the explicit form would be a way around constraints.
        Assert.Contains(Check("""
            interface Named { fn name(): string; }
            fn show<T :: [Named]>(x: T): string { return x.name(); }
            fn main(): int { let s = show<int>(5); return 0; }
            """).Diagnostics, d => d.Code == "LYR-SEM0028");

    // ------------------------------------------ constraints with their own type arguments

    private const string EqSetup = """
        pub interface Eq<T> {
            fn eq(other: T): bool;
        }

        extend int :: [Eq<int>] {
            fn eq(other: int): bool { return this == other; }
        }

        pub struct P :: [Eq<P>] {
            x: int,
            fn eq(other: P): bool { return this.x == other.x; }
        }

        pub fn same<T :: [Eq<T>]>(a: T, b: T): bool {
            return a.eq(b);
        }

        """;

    /// <summary>
    /// A constraint may bring its own type argument: <c>T :: [Eq&lt;T&gt;]</c>.
    ///
    /// <para>The point it failed at was worded helplessly: "cannot assign 'T' to 'T'". Two different
    /// symbols with the same name — the <c>T</c> of the function and the <c>T</c> of the interface.
    /// <c>MemberOfTypeParam</c> returned the raw interface type without substituting the constraint's
    /// arguments.</para>
    ///
    /// <para>The conformance check had always done the same substitution correctly: one question, two
    /// places, and only one had the answer.</para>
    ///
    /// <para>Without this, <c>Map&lt;K :: [Hashable&lt;K&gt;], V&gt;</c> cannot be written down.</para>
    /// </summary>
    [Fact]
    public void A_constraint_may_carry_its_own_type_argument() =>
        Assert.Equal(1, Run(EqSetup + """
            fn main(): int {
                if (same<int>(3, 3)) { return 1; }
                return 0;
            }
            """));

    [Fact]
    public void The_type_argument_may_be_inferred() =>
        // Without an explicit '<int>'. The inference used to run into the same fault.
        Assert.Equal(0, Run(EqSetup + """
            fn main(): int {
                if (same(3, 4)) { return 1; }
                return 0;
            }
            """));

    [Fact]
    public void A_user_type_satisfies_the_constraint() =>
        // A 'struct' with ':: [Eq<P>]' — the case Map and Set will need.
        Assert.Equal(1, Run(EqSetup + """
            fn main(): int {
                if (same(P { x = 7 }, P { x = 7 })) { return 1; }
                return 0;
            }
            """));

    [Fact]
    public void The_constraint_survives_being_passed_on() =>
        // Two generic functions in a row: 'describe<T>' passes its own T on to 'same<T>'. Without this
        // test it would stay unchecked whether the substitution is right when the argument is itself a
        // type parameter.
        Assert.Equal(1, Run(EqSetup + """
            pub fn describe<T :: [Eq<T>]>(a: T, b: T): int {
                if (same<T>(a, b)) { return 1; }
                return 0;
            }

            fn main(): int { return describe(5, 5); }
            """));

    // ------------------------------- an interface parameter on a generic function

    private const string IterSetup = """
        import std.iter { Iterator, RangeIterator };

        pub fn zaehle<T>(source: Iterator<T>): int {
            var n = 0;
            var v = source.next();
            while (v != null) { n = n + 1; v = source.next(); }
            return n;
        }

        """;

    /// <summary>
    /// A class as an argument where the generic signature demands an interface.
    ///
    /// <para>The compiler crashed here ("arg 0 is &amp;ty1, expected dyn ty0"). The parameter type
    /// <c>Iterator&lt;T&gt;</c> was lowered WITHOUT the call site's substitution, threw on the unresolved
    /// <c>T</c>, and the <c>catch</c> then passed the argument through WITHOUT a coercion — the class
    /// landed where a fat pointer had to stand.</para>
    ///
    /// <para>Non-generic calls were never affected, because the parameter type is directly lowerable
    /// there. It therefore stayed undetected until the first generic iterator adapter, and it blocked
    /// <c>std.iter</c> completely, where EVERY function looks like this.</para>
    /// </summary>
    [Fact]
    public void A_class_coerces_to_an_interface_parameter_of_a_generic_function() =>
        Assert.Equal(3, Run(IterSetup + """
            fn main(): int {
                return zaehle<int>(RangeIterator { current = 0, end = 3 });
            }
            """));

    /// <summary>
    /// The type argument is inferred THROUGH A CONFORMANCE.
    ///
    /// <para><c>count(RangeIterator { … })</c> concludes from
    /// <c>class RangeIterator :: [Iterator&lt;int&gt;]</c> that <c>T = int</c>. Structurally the two types
    /// have nothing in common; the connection stands in the declaration, and the unification looks it
    /// up.</para>
    ///
    /// <para>Without this case, exactly the functions in <c>std.iter</c> without a closure would be
    /// unusable — the ones at the end of a chain.</para>
    /// </summary>
    [Fact]
    public void Inference_works_through_a_conformance() =>
        Assert.Equal(2, Run(IterSetup + """
            fn main(): int {
                return zaehle(RangeIterator { current = 5, end = 7 });
            }
            """));

    [Fact]
    public void Inference_works_when_no_other_parameter_carries_the_type() =>
        // The sharper case: a second parameter contributing NOTHING to the inference. A closure beside it
        // ('map', 'filter') used to be the only help; here there is none.
        Assert.Equal(7, Run("""
            import std.iter { Iterator, RangeIterator };

            pub fn nimm<T>(source: Iterator<T>, n: int): int {
                var zahl = 0;
                var v = source.next();
                while (v != null && zahl < n) { zahl = zahl + 1; v = source.next(); }
                return zahl + 5;
            }

            fn main(): int {
                return nimm(RangeIterator { current = 0, end = 9 }, 2);
            }
            """));

    /// <summary>Compiles up to the lowering and returns the diagnostics, for cases meant to fail.
    /// </summary>
    private static string LoweringDiagnostics(string source)
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
        ModuleLowerer.Lower(comp, binding, types, de, verify: true);

        var writer = new StringWriter();
        de.RenderText(writer);
        return writer.ToString();
    }

    [Fact]
    public void A_generic_adapter_over_an_interface_works()
    {
        // The actual target case: a class with TWO type parameters satisfying an interface with ONE, and
        // with a closure as a field. That is the shape of every iter adapter.
        Assert.Equal(60, Run("""
            import std.iter { Iterator, RangeIterator };

            pub class MapIter<T, U> :: [Iterator<U>] {
                source: Iterator<T>,
                f: fn(T) -> U,

                pub mut fn next(): ?U {
                    let v = this.source.next();
                    if (v == null) { return null; }
                    let g = this.f;
                    return g(v);
                }
            }

            pub fn mapIter<T, U>(source: Iterator<T>, f: fn(T) -> U): Iterator<U> {
                return MapIter<T, U> { source = source, f = f };
            }

            fn main(): int {
                var summe = 0;
                for (x in mapIter(RangeIterator { current = 0, end = 4 }, (n: int) => n * 10)) {
                    summe = summe + x;
                }
                return summe;
            }
            """));
    }
}
