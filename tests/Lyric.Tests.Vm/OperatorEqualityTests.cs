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
/// <c>==</c> and <c>!=</c> on types that conform to <c>Equatable</c>, end to end.
///
/// <para>The operator IS <c>equals</c>: the sema records the call, the lowering emits it, and no new
/// opcode exists — which is why these run on the unchanged bytecode format. What these tests hold
/// is that the DISPATCH is right for every receiver shape the member path knows: a plain struct, an
/// enum, a conformance through an <c>extend</c> block, a generic instance, and a constrained type
/// parameter.</para>
/// </summary>
public class OperatorEqualityTests
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

    private const string Point = """
        import std.core { Equatable };

        struct Point :: [Equatable<Point>] {
            x: int,
            y: int,
            fn equals(other: Point): bool {
                return this.x == other.x && this.y == other.y;
            }
        }

        """;

    // ------------------------------------------------------------------ the receiver shapes

    [Fact]
    public void A_struct_compares_through_its_conformance()
    {
        Assert.Equal(1, Run(Point + """
            fn main(): int {
                let a = Point { x = 1, y = 2 };
                let b = Point { x = 1, y = 2 };
                return if (a == b) 1 else 0;
            }
            """));
    }

    [Fact]
    public void Inequality_is_the_negated_call()
    {
        Assert.Equal(1, Run(Point + """
            fn main(): int {
                let a = Point { x = 1, y = 2 };
                let b = Point { x = 9, y = 2 };
                return if (a != b) 1 else 0;
            }
            """));
    }

    [Fact]
    public void Both_answers_of_the_same_comparison_are_right()
    {
        // Equal and unequal through the same operator in one program: a desugar that inverted the
        // polarity or reused a temp would fail one of the two.
        Assert.Equal(10, Run(Point + """
            fn main(): int {
                let a = Point { x = 1, y = 2 };
                let same = Point { x = 1, y = 2 };
                let other = Point { x = 3, y = 2 };
                var score = 0;
                if (a == same) { score = score + 10; }
                if (a == other) { score = score + 100; }
                return score;
            }
            """));
    }

    [Fact]
    public void An_enum_compares_through_its_conformance()
    {
        Assert.Equal(1, Run("""
            import std.core { Equatable };

            enum Color :: [Equatable<Color>] {
                Red,
                Blue;

                fn equals(other: Color): bool {
                    let a = match (this) { Red => 0, Blue => 1 };
                    let b = match (other) { Red => 0, Blue => 1 };
                    return a == b;
                }
            }

            fn main(): int {
                let a = Color.Red;
                let b = Color.Red;
                return if (a == b) 1 else 0;
            }
            """));
    }

    [Fact]
    public void A_conformance_through_an_extend_block_counts()
    {
        // The type itself declares nothing; the extend block carries both the conformance and the
        // method. The operator has to find it the same way a written call would.
        Assert.Equal(1, Run("""
            import std.core { Equatable };

            struct Meter { value: int, }

            extend Meter :: [Equatable<Meter>] {
                fn equals(other: Meter): bool { return this.value == other.value; }
            }

            fn main(): int {
                let a = Meter { value = 3 };
                let b = Meter { value = 3 };
                return if (a == b) 1 else 0;
            }
            """));
    }

    [Fact]
    public void A_generic_instance_compares_through_its_conformance()
    {
        Assert.Equal(1, Run("""
            import std.core { Equatable };

            struct Box<T :: [Equatable<T>]> :: [Equatable<Box<T>>] {
                value: T,
                fn equals(other: Box<T>): bool { return this.value.equals(other.value); }
            }

            fn main(): int {
                let a = Box<int> { value = 7 };
                let b = Box<int> { value = 7 };
                return if (a == b) 1 else 0;
            }
            """));
    }

    [Fact]
    public void A_constrained_type_parameter_compares_inside_generic_code()
    {
        // 'a == b' on T, where only the constraint provides equals. Monomorphized per type: the
        // same generic function serves a struct and an int, and the int goes through the stdlib's
        // own 'extend int :: [Equatable<int>]'.
        Assert.Equal(2, Run(Point + """
            fn same<T :: [Equatable<T>]>(a: T, b: T): int {
                return if (a == b) 1 else 0;
            }

            fn main(): int {
                let points = same(Point { x = 1, y = 1 }, Point { x = 1, y = 1 });
                let ints = same(4, 4);
                return points + ints;
            }
            """));
    }

    // ------------------------------------------------------------------ nothing else moved

    [Fact]
    public void Scalar_comparisons_are_untouched()
    {
        // Primitives keep their opcodes; the desugar must not widen into them.
        Assert.Equal(1, Run("""
            fn main(): int {
                let n = 41 + 1;
                let s = "a" + "b";
                return if (n == 42 && s == "ab" && 1.5 != 2.5) 1 else 0;
            }
            """));
    }

    [Fact]
    public void The_operator_and_the_written_call_agree()
    {
        // The contract in one program: 'a == b' and 'a.equals(b)' are the same computation.
        Assert.Equal(1, Run(Point + """
            fn main(): int {
                let a = Point { x = 5, y = 6 };
                let b = Point { x = 5, y = 6 };
                return if ((a == b) == a.equals(b)) 1 else 0;
            }
            """));
    }

    [Fact]
    public void Operands_are_evaluated_once()
    {
        // The desugared call reuses the real operand nodes. If either were lowered twice, the
        // counter would pass 2. A captured 'var' is shared between closure and function, so the
        // writes inside 'make' are visible in 'main'.
        Assert.Equal(2, Run(Point + """
            fn main(): int {
                var calls = 0;
                let make = (x: int): Point => {
                    calls = calls + 1;
                    return Point { x = x, y = 0 };
                };
                let equal = make(1) == make(1);
                return if (equal) calls else -1;
            }
            """));
    }
}
