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
/// <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c> and <c>&gt;=</c> on types that conform to <c>Ordered</c>,
/// end to end.
///
/// <para>One method, four operators: the desugar calls <c>compare</c> once and reads the SIGN of
/// the answer against zero. The headline is <c>string &lt; string</c> — rejected since v1.0 with a
/// diagnostic that promised exactly this, and now admitted through the stdlib's own conformance
/// rather than through a string rule.</para>
/// </summary>
public class OperatorOrderingTests
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

    // ------------------------------------------------------------------ the headline

    [Fact]
    public void Strings_order_lexicographically()
    {
        Assert.Equal(1, Run("""
            fn main(): int {
                return if ("apple" < "banana") 1 else 0;
            }
            """));
    }

    [Fact]
    public void All_four_operators_work_on_strings()
    {
        // Each operator once, both answers observable: a desugar that read the wrong sign or the
        // wrong operator would tilt the sum.
        Assert.Equal(4, Run("""
            fn main(): int {
                var score = 0;
                if ("a" < "b")  { score = score + 1; }
                if ("a" <= "a") { score = score + 1; }
                if ("b" > "a")  { score = score + 1; }
                if ("b" >= "b") { score = score + 1; }
                if ("b" < "a")  { score = score + 100; }
                return score;
            }
            """));
    }

    [Fact]
    public void The_shorter_string_with_the_same_prefix_comes_first()
    {
        // The tie-break rule of the stdlib's compare, reached through the operator.
        Assert.Equal(1, Run("""
            fn main(): int {
                return if ("app" < "apple") 1 else 0;
            }
            """));
    }

    // ------------------------------------------------------------------ user types

    private const string Meter = """
        import std.core { Ordered };

        struct Meter :: [Ordered<Meter>] {
            value: int,
            fn compare(other: Meter): int {
                if (this.value < other.value) { return -1; }
                if (this.value > other.value) { return 1; }
                return 0;
            }
        }

        """;

    [Fact]
    public void A_struct_orders_through_its_conformance()
    {
        Assert.Equal(4, Run(Meter + """
            fn main(): int {
                let small = Meter { value = 1 };
                let big = Meter { value = 2 };
                var score = 0;
                if (small < big)   { score = score + 1; }
                if (small <= big)  { score = score + 1; }
                if (big > small)   { score = score + 1; }
                if (big >= big)    { score = score + 1; }
                if (big < small)   { score = score + 100; }
                return score;
            }
            """));
    }

    [Fact]
    public void The_operator_and_the_written_call_agree()
    {
        Assert.Equal(1, Run(Meter + """
            fn main(): int {
                let a = Meter { value = 3 };
                let b = Meter { value = 5 };
                return if ((a < b) == (a.compare(b) < 0)) 1 else 0;
            }
            """));
    }

    [Fact]
    public void A_constrained_type_parameter_orders_inside_generic_code()
    {
        // The same generic 'smallest' serves a struct, an int and a string; the int through its
        // opcode-free constraint route, the string through the stdlib extend.
        Assert.Equal(3, Run(Meter + """
            fn smallest<T :: [Ordered<T>]>(a: T, b: T): T {
                return if (a < b) a else b;
            }

            fn main(): int {
                let m = smallest(Meter { value = 7 }, Meter { value = 9 });
                let n = smallest(12, 30);
                let s = smallest("b", "a");
                var score = 0;
                if (m.value == 7) { score = score + 1; }
                if (n == 12)      { score = score + 1; }
                if (s == "a")     { score = score + 1; }
                return score;
            }
            """));
    }

    [Fact]
    public void Compare_is_called_once_per_comparison()
    {
        // The desugared call reuses the real operand nodes; a captured 'var' counts evaluations.
        Assert.Equal(2, Run(Meter + """
            fn main(): int {
                var made = 0;
                let make = (v: int): Meter => {
                    made = made + 1;
                    return Meter { value = v };
                };
                let ordered = make(1) < make(2);
                return if (ordered) made else -1;
            }
            """));
    }

    // ------------------------------------------------------------------ nothing else moved

    [Fact]
    public void Numeric_comparisons_are_untouched()
    {
        Assert.Equal(1, Run("""
            fn main(): int {
                let c = 'a';
                return if (1 < 2 && 2.5 > 1.5 && c < 'b' && 3 >= 3) 1 else 0;
            }
            """));
    }

    [Fact]
    public void Equality_and_ordering_desugar_side_by_side()
    {
        // '==' goes through Equatable, '<' through Ordered, on the same type in one expression.
        Assert.Equal(1, Run("""
            import std.core { Equatable, Ordered };

            struct Version :: [Equatable<Version>, Ordered<Version>] {
                n: int,
                fn equals(other: Version): bool { return this.n == other.n; },
                fn compare(other: Version): int {
                    if (this.n < other.n) { return -1; }
                    if (this.n > other.n) { return 1; }
                    return 0;
                }
            }

            fn main(): int {
                let v1 = Version { n = 1 };
                let v2 = Version { n = 2 };
                return if (v1 < v2 && v1 != v2) 1 else 0;
            }
            """));
    }
}
