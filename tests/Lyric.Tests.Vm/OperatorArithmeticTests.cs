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
/// <c>+</c>, <c>-</c>, <c>*</c> and <c>/</c> on types conforming to the arithmetic interfaces,
/// end to end.
///
/// <para><c>Vec2</c> is the receiver on purpose: it is the type the project's own measurements were
/// made with, back when vector maths through methods was the only form. <c>a.add(b)</c> becomes
/// <c>a + b</c>, and the cost is the same call the measurement priced.</para>
/// </summary>
public class OperatorArithmeticTests
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

    private const string Vec2 = """
        import std.core { Add, Sub, Mul };

        struct Vec2 :: [Add<Vec2>, Sub<Vec2>, Mul<Vec2>] {
            x: int,
            y: int,
            fn add(other: Vec2): Vec2 {
                return Vec2 { x = this.x + other.x, y = this.y + other.y };
            },
            fn sub(other: Vec2): Vec2 {
                return Vec2 { x = this.x - other.x, y = this.y - other.y };
            },
            fn mul(other: Vec2): Vec2 {
                return Vec2 { x = this.x * other.x, y = this.y * other.y };
            }
        }

        """;

    // ------------------------------------------------------------------ the operators

    [Fact]
    public void Vectors_add_with_a_plus()
    {
        Assert.Equal(46, Run(Vec2 + """
            fn main(): int {
                let a = Vec2 { x = 1, y = 2 };
                let b = Vec2 { x = 3, y = 40 };
                let sum = a + b;
                return sum.x + sum.y;
            }
            """));
    }

    [Fact]
    public void Sub_and_mul_reach_their_own_methods()
    {
        // Each operator its own method: a desugar that bound them all to one interface would give
        // the wrong answer here.
        // d = (7, 1), p = (30, 20): 7 + 1 + 30 + 20.
        Assert.Equal(58, Run(Vec2 + """
            fn main(): int {
                let a = Vec2 { x = 10, y = 5 };
                let b = Vec2 { x = 3, y = 4 };
                let d = a - b;
                let p = a * b;
                return d.x + d.y + p.x + p.y;
            }
            """));
    }

    [Fact]
    public void Operators_chain_and_keep_their_precedence()
    {
        // 'a + b * c' multiplies first: precedence is the parser's and unchanged by the desugar,
        // which sees finished trees.
        Assert.Equal(1 + 3 * 5 + (2 + 4 * 6), Run(Vec2 + """
            fn main(): int {
                let a = Vec2 { x = 1, y = 2 };
                let b = Vec2 { x = 3, y = 4 };
                let c = Vec2 { x = 5, y = 6 };
                let r = a + b * c;
                return r.x + r.y;
            }
            """));
    }

    [Fact]
    public void Division_reaches_div()
    {
        Assert.Equal(6, Run("""
            import std.core { Div };

            struct Ratio :: [Div<Ratio>] {
                n: int,
                fn div(other: Ratio): Ratio { return Ratio { n = this.n / other.n }; }
            }

            fn main(): int {
                let a = Ratio { n = 24 };
                let b = Ratio { n = 4 };
                let q = a / b;
                return q.n;
            }
            """));
    }

    [Fact]
    public void The_operator_and_the_written_call_agree()
    {
        Assert.Equal(1, Run(Vec2 + """
            fn main(): int {
                let a = Vec2 { x = 2, y = 3 };
                let b = Vec2 { x = 4, y = 5 };
                let viaOp = a + b;
                let viaCall = a.add(b);
                return if (viaOp.x == viaCall.x && viaOp.y == viaCall.y) 1 else 0;
            }
            """));
    }

    // ------------------------------------------------------------------ generic code

    [Fact]
    public void A_constrained_sum_serves_every_conforming_type()
    {
        // The stdlib conforms the numerics and string to Add, so one generic 'total' takes an int,
        // a float-free string concat, and a user vector — monomorphized three ways.
        Assert.Equal(1, Run(Vec2 + """
            import std.string { length };

            fn total<T :: [Add<T>]>(a: T, b: T, c: T): T {
                return a + b + c;
            }

            fn main(): int {
                let n = total(1, 2, 3);
                let s = total("a", "b", "c");
                let v = total(
                    Vec2 { x = 1, y = 0 },
                    Vec2 { x = 2, y = 0 },
                    Vec2 { x = 3, y = 0 });
                return if (n == 6 && length(s) == 3 && v.x == 6) 1 else 0;
            }
            """));
    }

    // ------------------------------------------------------------------ nothing else moved

    [Fact]
    public void Numeric_and_string_arithmetic_are_untouched()
    {
        Assert.Equal(1, Run("""
            import std.string { length };
            fn main(): int {
                let n = 2 + 3 * 4;
                let f = 10.0 / 4.0;
                let s = "ab" + "cd";
                let r = "x" * 3;
                let xs = [1] + [2, 3];
                return if (n == 14 && f == 2.5 && length(s) == 4 && length(r) == 3
                    && xs.length == 3) 1 else 0;
            }
            """));
    }

    [Fact]
    public void A_compound_through_the_interface_works_on_a_variable_target()
    {
        // The limit v1.5.0 recorded here is closed: 'v += w' on an identifier target IS the
        // stored operator call, lowered whole. CompoundOperatorTests carries the full set —
        // captured variables, the field-target diagnostic, the untouched repetition opcode.
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", Vec2 + """
            fn main(): int {
                var v = Vec2 { x = 1, y = 1 };
                v += Vec2 { x = 2, y = 2 };
                return v.x;
            }
            """);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);

        Assert.False(de.HasErrors, string.Join("; ",
            de.Diagnostics.Select(d => d.Code + ": " + d.Message)));
    }
}
