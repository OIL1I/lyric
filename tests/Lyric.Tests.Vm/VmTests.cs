using System.Runtime.CompilerServices;
using System.Text;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Tests for the interpreter.
///
/// <para>These are the first tests that check whether a program DOES the right thing; up to here
/// only correct translation could be checked. They therefore run over the whole pipeline: source,
/// sema, IR, bytecode, execution. A fault in any stage shows here.</para>
///
/// <para>The RETURN VALUE is checked rather than the process exit code: that one is masked to a byte
/// and would make negative values and overflows unrecognisable — exactly the interesting cases.</para>
/// </summary>
public class VmTests
{
    // ------------------------------------------------------------------ helpers

    private static LyrValue Run(string source)
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
        Assert.False(de.HasErrors, "source did not compile:\n" + writer.ToString());

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!));
        return Interpreter.Run(module, NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null));
    }

    /// <summary>Shorthand: the body is wrapped in a `main`.</summary>
    private static long Eval(string body) => Run($"fn main(): int {{ {body} }}").AsI64;

    [Fact]
    public void A_backtrace_names_the_line_that_panicked_and_the_line_that_called()
    {
        // 'divide' is small and is INLINED, so the backtrace has one frame: the caller's name with
        // the faulting line of the callee — spliced instructions keep their spans, which is what
        // preserves the line. The frame that would name 'divide' is the price of inlining, the
        // same trade every optimizing compiler makes. A function sealed by 'panic' never returns
        // and is not inlined, so a deliberate panic keeps its full backtrace.
        var panic = PanicWithSourceMap("""
            fn divide(a: int, b: int): int {
                return a / b;
            }

            fn main(): int {
                let n = 0;
                return divide(10, n);
            }
            """);

        Assert.Equal(["main.main (test.lyr:2)"], panic.CallStack);
    }

    [Fact]
    public void The_faulting_instruction_is_the_one_before_the_pointer()
    {
        // The test above cannot see the difference: there the faulting 'div' is followed by the
        // 'retval' of the SAME return statement, so Ip and Ip - 1 land on one line and a wrong
        // implementation stays green.
        //
        // Here the expression is pulled onto its own line. The 'div' is the last instruction line 4
        // produces; the 'retval' after it belongs to the return statement and carries line 3. The
        // two answers are now distinguishable, and only Ip - 1 gives the arithmetic that failed.
        var panic = PanicWithSourceMap("""
            fn main(): int {
                let n = 0;
                return
                    10 / n;
            }
            """);

        Assert.Equal(["main.main (test.lyr:4)"], panic.CallStack);
    }

    [Fact]
    public void Without_a_source_map_a_backtrace_is_names_only()
    {
        // The same program through the ordinary path: a stripped module still produces a backtrace,
        // just without positions. That is the whole cost of stripping. One frame, not two —
        // 'divide' is inlined, see the test above.
        var panic = RunExpectingPanic("""
            fn divide(a: int, b: int): int {
                return a / b;
            }

            fn main(): int {
                let n = 0;
                return divide(10, n);
            }
            """);

        Assert.Equal(["main.main"], panic.CallStack);
    }

    /// <summary>A programming error at runtime is a <c>panic</c>: not catchable, with a backtrace. No
    /// separate VM error path beside it.</summary>
    private static LyricPanic RunExpectingPanic(string source) =>
        Assert.Throws<LyricPanic>(() => Run(source));

    /// <summary>Like <see cref="RunExpectingPanic"/>, but the module carries a source map, so the
    /// backtrace can name lines.</summary>
    private static LyricPanic PanicWithSourceMap(string source)
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
        Assert.False(de.HasErrors, "source did not compile:\n" + writer.ToString());

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        var module = BytecodeReader.ReadOrThrow(
            BytecodeWriter.Write(ir!, new SourceMapContext(sm, Directory.GetCurrentDirectory())));

        return Assert.Throws<LyricPanic>(() =>
            Interpreter.Run(module, NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)));
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>Like <see cref="Run"/>, but with the stdlib on the module path and the built-in
    /// natives, for examples using <c>println</c>. The output is collected rather than written to
    /// <c>Console</c>.</summary>
    private static (LyrValue Result, string Output) RunWithStdlib(string source)
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
        Assert.False(de.HasErrors, "source did not compile:\n" + writer.ToString());

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);

        var output = new StringWriter();
        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!));
        var result = Interpreter.Run(module, NativeRegistry.CreateDefault(output, TextWriter.Null));
        return (result, output.ToString());
    }

    // ------------------------------------------------------------------ 1) the gate program

    /// <summary>Objects over the whole pipeline, including reference semantics across a function
    /// boundary and a class as a field type.</summary>
    [Fact]
    public void Object_gate_program_computes_the_right_answer()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "examples", "objects.lyr"), Encoding.UTF8);
        var (result, output) = RunWithStdlib(source);

        // 10, twice +5 through bump (the mutation is visible at the caller), then +1 through the alias.
        Assert.Equal(21, result.AsI64);
        Assert.Equal("verschachtelt: 21\n", output.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Gate_program_computes_the_right_answer()
    {
        // sumTo(10) = 55, gcd(48,18) = 6, max(55,6) = 55, add(55,0) = 55.
        // The first real proof that the pipeline does not only translate but computes.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "examples", "arith.lyr"), Encoding.UTF8);
        Assert.Equal(55, Run(source).AsI64);
    }

    // ------------------------------------------------------------------ 2) Rechnen

    [Theory]
    [InlineData("return 1 + 2 * 3;", 7)]                  // precedence
    [InlineData("return (1 + 2) * 3;", 9)]
    [InlineData("return 7 / 2;", 3)]                      // Ganzzahldivision schneidet ab
    [InlineData("return -7 / 2;", -3)]                    // towards zero, not towards minus infinity
    [InlineData("return -7 % 2;", -1)]                    // the remainder carries the sign of the dividend
    [InlineData("return 7 % -2;", 1)]
    [InlineData("return 1 << 10;", 1024)]
    [InlineData("return -16 >> 2;", -4)]                  // an arithmetic shift for signed operands
    [InlineData("return 12 & 10;", 8)]
    [InlineData("return 12 | 10;", 14)]
    [InlineData("return 12 ^ 10;", 6)]
    [InlineData("return ~0;", -1)]
    [InlineData("return -(-5);", 5)]
    public void Integer_arithmetic(string body, long expected) => Assert.Equal(expected, Eval(body));

    [Fact]
    public void Signed_min_divided_by_minus_one_wraps()
    {
        // Two's complement has no positive counterpart to MinValue. .NET throws here; Lyric wraps, as
        // for every other integer operation.
        Assert.Equal(long.MinValue, Eval("let m: int = -9223372036854775807 - 1; return m / -1;"));
    }

    [Theory]
    // The shift amount is taken modulo the OPERAND WIDTH. Masking at 64 and normalizing to the target
    // width instead is a mixture that makes the same rule differ by type: `1 << 9` yields 0 for int8
    // and 2 for int64.
    [InlineData("let a: int8 = 1; let s: int8 = 9; return (a << s) as int;", 2)]     // 9 mod 8 = 1
    [InlineData("let a: int32 = 1; let s: int32 = 33; return (a << s) as int;", 2)]  // 33 mod 32 = 1
    [InlineData("let a: int = 1; let s: int = 65; return a << s;", 2)]               // 65 mod 64 = 1
    [InlineData("let a: int8 = 1; let s: int8 = 7; return (a << s) as int;", -128)]  // Vorzeichenbit
    public void Shift_count_is_taken_modulo_the_operand_width(string body, long expected) =>
        Assert.Equal(expected, Eval(body));

    [Fact]
    public void Narrow_integers_wrap_at_their_own_width()
    {
        // Without width normalization after every operation this would yield 200 instead of -56.
        Assert.Equal(-56, Eval("let a: int8 = 100; let b: int8 = 100; return (a + b) as int;"));
    }

    [Fact]
    public void Unsigned_comparison_is_not_signed_comparison()
    {
        // Read as u64, 0xFFFF… is the largest value; as i64 it would be -1. The tag on the opcode
        // decides, which is why it carries the operand type rather than the result type.
        Assert.Equal(1, Eval("let big: uint = 18446744073709551615; let one: uint = 1; " +
                             "return if (big > one) 1 else 0;"));
    }

    // ------------------------------------------------------------------ 3) conversion

    [Theory]
    [InlineData("let n: int = 300; return n as int8 as int;", 44)]      // 300 & 0xFF = 44
    [InlineData("let n: int = -1; return n as uint8 as int;", 255)]
    [InlineData("let f: float = 3.9; return f as int;", 3)]            // towards zero
    [InlineData("let f: float = -3.9; return f as int;", -3)]
    [InlineData("let n: int32 = 7; return n as int64 as int;", 7)]
    public void Conversions(string body, long expected) => Assert.Equal(expected, Eval(body));

    [Fact]
    public void Float_to_int_saturates_instead_of_being_undefined()
    {
        // WASM's trunc_sat behaviour. The alternative would be "undefined as in C", under which the same
        // .lyrbc file would give different results on two runtimes and the promise of a second
        // implementation would be worth nothing.
        Assert.Equal(long.MaxValue, Eval("let f: float = 1e30; return f as int;"));
        Assert.Equal(long.MinValue, Eval("let f: float = -1e30; return f as int;"));
    }

    [Fact]
    public void Float32_arithmetic_uses_single_precision()
    {
        // 2^24 is the first integer from which f32 can no longer count every one: 16777216 + 1 stays
        // 16777216. Computed in double precision the result would be 16777217 and the comparison would
        // fail, so the test really distinguishes the computation width.
        Assert.Equal(1, Eval("""
            let big: float32 = 16777216.0f32;
            let plusOne: float32 = big + 1.0f32;
            return if (plusOne == big) 1 else 0;
            """));
    }

    // ------------------------------------------------------------------ 4) Kontrollfluss

    [Theory]
    [InlineData("var i = 0; var s = 0; while (i < 5) { s += i; i += 1; } return s;", 10)]
    [InlineData("var i = 3; var s = 0; do { s += i; i -= 1; } while (i > 0); return s;", 6)]
    [InlineData("var i = 0; while (true) { i += 1; if (i > 3) { break; } } return i;", 4)]
    [InlineData("var i = 0; var s = 0; while (i < 5) { i += 1; if (i % 2 == 0) { continue; } s += i; } return s;", 9)]
    [InlineData("return if (1 < 2) 10 else 20;", 10)]
    [InlineData("return if (1 > 2) 10 else 20;", 20)]
    public void Control_flow(string body, long expected) => Assert.Equal(expected, Eval(body));

    [Fact]
    public void And_short_circuits_before_evaluating_the_right_side()
    {
        // The proof runs over a side effect that does not otherwise exist: were the right side
        // evaluated, there would be a division by zero and therefore a panic instead of a 7.
        Assert.Equal(7, Eval("var d = 0; if (false && (10 / d) > 0) { return 1; } return 7;"));
    }

    [Fact]
    public void Or_short_circuits_before_evaluating_the_right_side()
    {
        Assert.Equal(7, Eval("var d = 0; if (true || (10 / d) > 0) { return 7; } return 1;"));
    }

    [Fact]
    public void Recursion_and_forward_calls()
    {
        Assert.Equal(3628800, Run("""
            fn fact(n: int): int {
                if (n <= 1) { return 1; }
                return n * fact(n - 1);
            }
            fn main(): int { return fact(10); }
            """).AsI64);
    }

    [Fact]
    public void Postfix_and_prefix_increment_differ()
    {
        // i++ yields the old value, ++i the new one. Both write the same slot.
        Assert.Equal(0, Eval("var i = 0; let old = i++; return old;"));
        Assert.Equal(1, Eval("var i = 0; let now = ++i; return now;"));
    }

    // ------------------------------------------------------------------ 4b) Objekte

    private const string Counter = "class Counter { value: int, step: int }\n";

    [Fact]
    public void An_object_carries_its_fields()
    {
        Assert.Equal(10, Run(Counter +
            "fn main(): int { let c = Counter { value = 10, step = 5 }; return c.value; }").AsI64);
        Assert.Equal(5, Run(Counter +
            "fn main(): int { let c = Counter { value = 10, step = 5 }; return c.step; }").AsI64);
    }

    [Fact]
    public void A_field_can_be_assigned_and_compound_assigned()
    {
        Assert.Equal(3, Run(Counter +
            "fn main(): int { let c = Counter { value = 1, step = 2 }; c.value = 3; return c.value; }").AsI64);
        Assert.Equal(3, Run(Counter +
            "fn main(): int { let c = Counter { value = 1, step = 2 }; c.value += c.step; return c.value; }").AsI64);
    }

    /// <summary>
    /// The test that distinguishes classes from structs. A class is a reference type: two names for the
    /// same object see each other. If a copy on assignment were added later, exactly this test fails —
    /// and only this one.
    /// </summary>
    [Fact]
    public void Assignment_copies_the_reference_not_the_object()
    {
        Assert.Equal(99, Run(Counter +
            """
            fn main(): int {
                let c = Counter { value = 1, step = 0 };
                let alias = c;
                alias.value = 99;
                return c.value;
            }
            """).AsI64);
    }

    /// <summary>The same across a function boundary: the argument is the reference, so the mutation is
    /// visible at the caller. Without this case "reference semantics" would only be shown
    /// locally.</summary>
    [Fact]
    public void An_object_passed_to_a_function_is_mutated_in_place()
    {
        Assert.Equal(7, Run(Counter +
            """
            fn bump(c: Counter) {
                c.value += c.step;
            }

            fn main(): int {
                let c = Counter { value = 4, step = 3 };
                bump(c);
                return c.value;
            }
            """).AsI64);
    }

    [Fact]
    public void A_field_of_class_type_nests()
    {
        Assert.Equal(42, Run(
            """
            class Inner { value: int }
            class Outer { inner: Inner }

            fn main(): int {
                let outer = Outer { inner = Inner { value = 42 } };
                return outer.inner.value;
            }
            """).AsI64);
    }

    /// <summary>Two separately created objects share nothing — the counter-check to the alias test,
    /// without which a global store per type would be indistinguishable from the tests above.</summary>
    [Fact]
    public void Two_instances_are_independent()
    {
        Assert.Equal(1, Run(Counter +
            """
            fn main(): int {
                let a = Counter { value = 1, step = 0 };
                let b = Counter { value = 2, step = 0 };
                b.value = 50;
                return a.value;
            }
            """).AsI64);
    }

    // ------------------------------------------------------------------ 4c) Methoden (ADR-014)

    private const string Acc = """
        class Acc {
            total: int,

            static fn new(start: int): Acc { return Acc { total = start }; }
            fn get(): int { return this.total; }
            fn add(n: int) { this.total += n; }
            fn addTwice(n: int) { this.add(n); this.add(n); }
        }

        """;

    [Fact]
    public void A_static_factory_constructs_and_an_instance_method_reads()
    {
        Assert.Equal(7, Run(Acc + "fn main(): int { return Acc.new(7).get(); }").AsI64);
    }

    /// <summary>The receiver is parameter 0: a method mutates the same object the caller holds. Without
    /// the right argument order this yields nonsense, and silent nonsense at that, because both
    /// arguments are numbers.</summary>
    [Fact]
    public void An_instance_method_mutates_the_receiver()
    {
        Assert.Equal(10, Run(Acc +
            "fn main(): int { let a = Acc.new(7); a.add(3); return a.get(); }").AsI64);
    }

    /// <summary>A method calls a method on the same <c>this</c>. Checks that the receiver is an ordinary
    /// value in the body and can be passed on.</summary>
    [Fact]
    public void A_method_can_call_another_method_on_this()
    {
        Assert.Equal(11, Run(Acc +
            "fn main(): int { let a = Acc.new(5); a.addTwice(3); return a.get(); }").AsI64);
    }

    /// <summary>Two instances, the same method: the receiver decides, not the function. If this test
    /// fails, all instances accidentally share one state.</summary>
    [Fact]
    public void Methods_act_on_their_own_receiver()
    {
        Assert.Equal(1, Run(Acc +
            """
            fn main(): int {
                let a = Acc.new(1);
                let b = Acc.new(2);
                b.add(50);
                return a.get();
            }
            """).AsI64);
    }

    // ------------------------------------------------------------------ 4d) Arrays (ADR-016)

    [Theory]
    [InlineData("let xs = [3, 7, 1]; return xs[1];", 7)]
    [InlineData("let xs = [3, 7, 1]; return xs.length;", 3)]
    [InlineData("let xs = [0] * 4; return xs.length;", 4)]        // an array from a default
    [InlineData("let n = 5; let xs = [0] * n; return xs.length;", 5)]  // length at runtime
    [InlineData("let xs = [0] * 0; return xs.length;", 0)]        // an empty array is valid
    [InlineData("let xs = [1, 2] + [3]; return xs[2];", 3)]
    [InlineData("let xs = [1, 2] + [3]; return xs.length;", 3)]
    [InlineData("let xs = [7] * 3; return xs[0] + xs[1] + xs[2];", 21)]
    [InlineData("var xs = [1, 2, 3]; xs[1] = 9; return xs[1];", 9)]
    [InlineData("var xs = [1, 2, 3]; xs[1] += 9; return xs[1];", 11)]
    public void Arrays_behave(string body, long expected) => Assert.Equal(expected, Eval(body));

    /// <summary>Concatenation yields a NEW array: a <c>T[]</c> does not grow, so the operand must not be
    /// changed along with it.</summary>
    [Fact]
    public void Concatenation_leaves_its_operands_alone()
    {
        Assert.Equal(2, Eval("let xs = [1, 2]; let ys = xs + [3]; return xs.length;"));
    }

    /// <summary>An array is a reference, like a class: two names, one store.</summary>
    [Fact]
    public void An_array_is_a_reference()
    {
        Assert.Equal(9, Eval("var xs = [1, 2]; var ys = xs; ys[0] = 9; return xs[0];"));
    }

    /// <summary>
    /// An element index is a RUNTIME VALUE: unlike type and field indices the loader cannot check it. A
    /// violation is therefore a <c>panic</c> with a backtrace, not a load error and certainly not a
    /// silent memory access.
    /// </summary>
    [Theory]
    [InlineData("let xs = [1, 2]; return xs[2];")]
    [InlineData("let xs = [1, 2]; return xs[-1];")]
    [InlineData("let xs = [0] * 0; return xs[0];")]
    [InlineData("var xs = [1, 2]; xs[5] = 0; return 0;")]
    public void An_index_outside_the_array_panics(string body)
    {
        var panic = RunExpectingPanic($"fn main(): int {{ {body} }}");
        Assert.Equal(VmDiagnostics.IndexOutOfRange, panic.Code);
        Assert.NotEmpty(panic.CallStack);
    }

    [Fact]
    public void A_negative_repetition_count_panics()
    {
        var panic = RunExpectingPanic("fn main(): int { let n = -1; let xs = [0] * n; return 0; }");
        Assert.Equal(VmDiagnostics.IndexOutOfRange, panic.Code);
    }

    // ------------------------------------------------------------------ 4e) Optionals (§7)

    private const string Find = "fn find(x: int): ?int { if (x > 0) { return x; } return null; }\n";

    [Theory]
    [InlineData("return find(7) ?? 0;", 7)]
    [InlineData("return find(-1) ?? 100;", 100)]     // the right side only on "no value"
    [InlineData("return find(7)!;", 7)]
    [InlineData("let m = find(3); if (m != null) { return m; } return 0;", 3)]   // Narrowing
    [InlineData("let m = find(-1); if (m == null) { return 42; } return 0;", 42)]
    [InlineData("let m = find(-1); if (m != null) { return m; } return 0;", 0)]
    public void Optionals_behave(string body, long expected) =>
        Assert.Equal(expected,
            Run(Find + $"fn wrap(): int {{ {body} }}\nfn main(): int {{ return wrap(); }}").AsI64);

    /// <summary>
    /// The core of the representation decision: a <c>?int</c> has to carry ALL <c>int</c> values. Were
    /// any bit pattern reserved as "null" — 0 and -1 being the usual candidates — that very value would
    /// be a value on one runtime and no value on another. The format forbids it, and this test holds it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public void An_optional_int_can_carry_every_int(long value)
    {
        var source = $"fn wrap(): ?int {{ return {value}; }}\n" +
                     "fn main(): int { let x = wrap(); if (x != null) { return 1; } return 0; }";
        Assert.Equal(1, Run(source).AsI64);
        Assert.Equal(value, Run($"fn wrap(): ?int {{ return {value}; }}\n" +
                                "fn main(): int { return wrap()!; }").AsI64);
    }

    /// <summary>The right side of <c>??</c> is NOT evaluated when there is a value on the left; otherwise
    /// it would be no short circuit. Shown through a division by zero that would otherwise
    /// panic.</summary>
    [Fact]
    public void Coalescing_does_not_evaluate_its_right_side_when_there_is_a_value()
    {
        Assert.Equal(7, Run(Find + "fn boom(): int { return 1 / 0; }\n" +
                            "fn wrap(): int { return find(7) ?? boom(); }\n" +
                            "fn main(): int { return wrap(); }").AsI64);
    }

    [Fact]
    public void Force_unwrapping_nothing_panics()
    {
        var panic = RunExpectingPanic(Find + "fn main(): int { return find(-1)!; }");
        Assert.Equal(VmDiagnostics.NullDereference, panic.Code);
        Assert.NotEmpty(panic.CallStack);
    }

    // ------------------------------------------------------------------ 4f) Enums (§3.4)

    private const string ShapeEnum = """
        enum Shape {
            Circle(int),
            Rect { w: int, h: int },
            Empty;

            fn area(): int {
                return match (this) {
                    Circle(r) => r * r,
                    Rect { w, h } => w * h,
                    Empty => 0,
                };
            }
        }

        """;

    [Theory]
    [InlineData("return Shape.Circle(5).area();", 25)]                       // tuple variant
    [InlineData("let s: Shape = Shape.Rect { w = 3, h = 4 }; return s.area();", 12)] // struct variant
    [InlineData("return Shape.Empty.area();", 0)]                            // a unit variant
    public void Enum_variants_dispatch_through_match(string body, long expected) =>
        Assert.Equal(expected, Run(ShapeEnum + $"fn wrap(): int {{ {body} }}\nfn main(): int {{ return wrap(); }}").AsI64);

    /// <summary>Every variant carries its own fields: the payload of one must not show through when
    /// reading another. That is the invariant behind "one layout per variant".</summary>
    [Fact]
    public void Variants_keep_their_own_payload()
    {
        Assert.Equal(37, Run(ShapeEnum +
            """
            fn main(): int {
                let a = Shape.Circle(5);
                let b: Shape = Shape.Rect { w = 3, h = 4 };
                return a.area() + b.area();
            }
            """).AsI64);
    }

    /// <summary><c>match</c> as a STATEMENT: the same code as for the expression, only without the
    /// result slot.</summary>
    [Fact]
    public void Match_works_as_a_statement()
    {
        Assert.Equal(9, Run(ShapeEnum +
            """
            fn main(): int {
                var total = 0;
                let s = Shape.Circle(3);
                match (s) {
                    Circle(r) => { total = r * r; },
                    Rect { w, h } => { total = 1; },
                    Empty => { total = 2; },
                }
                return total;
            }
            """).AsI64);
    }

    // ------------------------------------------------------------------ 5) Laufzeitfehler

    [Fact]
    public void Division_by_zero_panics_with_a_backtrace()
    {
        // A broken contract is a panic, not a separate VM error path beside it; otherwise there would be
        // three error mechanisms instead of two.
        var ex = RunExpectingPanic("""
            fn divide(a: int, b: int): int { return a / b; }
            fn main(): int { var zero = 0; return divide(1, zero); }
            """);

        Assert.Equal(VmDiagnostics.DivisionByZero, ex.Code);
        // One frame: 'divide' is inlined into main, and the backtrace names the surviving frame.
        Assert.Equal(new[] { "main.main" }, ex.CallStack);
    }

    [Fact]
    public void Remainder_by_zero_panics()
    {
        var ex = RunExpectingPanic("fn main(): int { var d = 0; return 10 % d; }");
        Assert.Equal(VmDiagnostics.DivisionByZero, ex.Code);
    }

    [Fact]
    public void Float_division_by_zero_is_infinity_not_an_error()
    {
        // IEEE 754: no exception but Inf. Only integers know the error. (`main` has to return an int, so
        // the comparison happens inside.)
        Assert.Equal(1, Eval("var d = 0.0; let r = 1.0 / d; return if (r > 1.0e308) 1 else 0;"));
    }

    [Fact]
    public void Runaway_recursion_is_reported_instead_of_crashing_the_process()
    {
        // With .NET recursion in the interpreter this would be a StackOverflowException, and that cannot
        // be caught in .NET: the process dies. Hence an explicit frame stack.
        var ex = RunExpectingPanic("""
            fn down(n: int): int { return down(n + 1); }
            fn main(): int { return down(0); }
            """);
        Assert.Equal(VmDiagnostics.CallDepthExceeded, ex.Code);
    }

    [Fact]
    public void A_module_without_main_has_no_entry_point()
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("lib.lyr", "pub fn helper(): int { return 1; }");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);
        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true)!;

        Assert.Null(ir.EntryFunction);

        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir));
        Assert.Null(module.Start);

        var ex = Assert.Throws<LyricRuntimeException>(() => Interpreter.Run(module, NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)));
        Assert.Equal(VmDiagnostics.NoEntryPoint, ex.Code);
    }

    // ------------------------------------------------------------------ 6) the Start section

    [Fact]
    public void Start_section_survives_the_round_trip()
    {
        // Without this section a runtime would have to guess the entry from a naming convention, and a
        // second implementation knowing only the specification could not find it at all.
        var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(
            LowerOnly("fn helper(): int { return 1; } fn main(): int { return helper(); }")));

        Assert.NotNull(module.Start);
        Assert.Equal("main.main", module.Functions[module.Start!.Value].Name);
    }

    private static Lyric.Ir.IrModule LowerOnly(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);
        Assert.False(de.HasErrors);
        return ModuleLowerer.Lower(comp, binding, types, de, verify: true)!;
    }

    // ------------------------------------------------------------------ ?. and ??=

    /// <summary><c>??=</c> assigns only when there is nothing there, and evaluates the right side only
    /// then.</summary>
    [Theory]
    [InlineData("null", 5)]
    [InlineData("1", 1)]
    public void Coalescing_assign_only_fills_an_empty_optional(string initial, long expected) =>
        Assert.Equal(expected, Run($"fn main(): int {{ var x: ?int = {initial}; x ??= 5; return x!; }}").AsI64);

    [Fact]
    public void Coalescing_assign_does_not_evaluate_its_right_side_when_full()
    {
        // The short circuit is shown through a division by zero that would otherwise fire — the same form
        // that already covered '??'.
        Assert.Equal(1, Run("""
            fn main(): int {
                var zero = 0;
                var x: ?int = 1;
                x ??= 1 / zero;
                return x!;
            }
            """).AsI64);
    }

    /// <summary><c>?.</c> accesses only when the carrier has a value; the result is always an
    /// optional.</summary>
    [Theory]
    [InlineData("null", 0)]
    [InlineData("P { n = 7 }", 7)]
    public void Optional_chaining_skips_the_access_when_empty(string initial, long expected) =>
        Assert.Equal(expected, Run($$"""
            class P { n: int }

            fn main(): int {
                let p: ?P = {{initial}};
                let n: ?int = p?.n;
                return n ?? 0;
            }
            """).AsI64);

    // ------------------------------------------------------------------ P5b: string-Ops, panic

    /// <summary><c>+</c> and <c>*</c> on <c>string</c> are built-in semantics but no opcode: they lower
    /// to calls in <c>std.string</c>, or <c>add</c> would be polymorphic.</summary>
    [Fact]
    public void String_concatenation_lowers_to_a_call()
    {
        var (_, output) = RunWithStdlib("""
            import std.io.console;
            fn main(): int { console.print("a" + "b" + "c"); return 0; }
            """);
        Assert.Equal("abc", output);
    }

    [Theory]
    [InlineData("3", "ababab")]
    [InlineData("0", "")]
    [InlineData("0 - 1", "")]   // negative is no error case; the specification knows none
    public void String_repetition_lowers_to_a_call(string count, string expected)
    {
        var (_, output) = RunWithStdlib($$"""
            import std.io.console;
            fn main(): int { console.print("ab" * ({{count}})); return 0; }
            """);
        Assert.Equal(expected, output);
    }

    /// <summary><c>panic</c> is a language builtin with the return type <c>never</c>: not catchable, and
    /// it ends the VM with a backtrace.</summary>
    [Fact]
    public void Panic_aborts_with_its_message_and_a_backtrace()
    {
        var panic = Assert.Throws<LyricPanic>(() => RunWithStdlib("""
            fn deep(): int { panic("kaputt"); }
            fn main(): int { return deep(); }
            """));

        Assert.Equal(VmDiagnostics.Panicked, panic.Code);
        Assert.Equal("kaputt", panic.Message);
        // The backtrace names both frames, innermost first.
        Assert.Equal(["main.deep", "main.main"], panic.CallStack);
    }

    [Fact]
    public void Code_after_a_panic_is_unreachable_but_the_other_branch_still_runs()
    {
        // 'panic' seals its block, and the return value of LowerStmt has to report that, or the caller
        // tries to seal the same block a second time.
        Assert.Equal(5, RunWithStdlib("""
            fn f(n: int): int {
                if (n < 0) { panic("negativ"); }
                return n;
            }
            fn main(): int { return f(5); }
            """).Result.AsI64);
    }

    // ------------------------------------------------------------------ P5b: Defaults, params

    /// <summary>
    /// Default values are materialized AT THE CALL SITE rather than at the callee: the IR knows no
    /// optional parameters, and after the lowering a call is a call.
    /// </summary>
    [Theory]
    [InlineData("f(1)", 3)]        // b faellt weg -> Default 2
    [InlineData("f(1, 10)", 11)]   // b angegeben -> Default ungenutzt
    public void A_default_fills_an_omitted_trailing_argument(string call, long expected) =>
        Assert.Equal(expected, Run($$"""
            fn f(a: int, b: int = 2): int { return a + b; }
            fn main(): int { return {{call}}; }
            """).AsI64);

    [Fact]
    public void Several_defaults_fill_from_the_right()
    {
        Assert.Equal(6, Run("""
            fn f(a: int, b: int = 2, c: int = 3): int { return a + b + c; }
            fn main(): int { return f(1); }
            """).AsI64);
    }

    [Fact]
    public void A_default_is_evaluated_per_call_not_once()
    {
        // At the call site means: called twice, evaluated twice. Were the default lowered once at the
        // callee, both calls would share one object.
        Assert.Equal(2, Run("""
            class Cell { n: int }
            fn make(c: Cell = Cell { n = 0 }): int { c.n += 1; return c.n; }
            fn main(): int { make(); make(); return make() + 1; }
            """).AsI64);
    }

    /// <summary><c>params</c> collects the rest into an array, another call-site transformation: the
    /// callee sees an ordinary <c>T[]</c>.</summary>
    [Theory]
    [InlineData("sum(1, 2, 3)", 6)]
    [InlineData("sum()", 0)]        // an empty array, not a special case
    [InlineData("sum(5)", 5)]
    public void Params_collects_the_remaining_arguments(string call, long expected) =>
        Assert.Equal(expected, Run($$"""
            fn sum(params xs: int[]): int {
                var total = 0;
                var i = 0;
                while (i < xs.length) { total += xs[i]; i += 1; }
                return total;
            }
            fn main(): int { return {{call}}; }
            """).AsI64);

    [Fact]
    public void Params_follows_the_fixed_parameters()
    {
        Assert.Equal(7, Run("""
            fn tag(n: int, params xs: int[]): int { return n * 3 + xs.length; }
            fn main(): int { return tag(2, 5); }
            """).AsI64);
    }

    private const string VariadicSum = """
        fn sum(params xs: int[]): int {
            var total = 0;
            var i = 0;
            while (i < xs.length) { total += xs[i]; i += 1; }
            return total;
        }
        """;

    /// <summary>
    /// The case that motivates the rule: without passing through, a variadic function cannot delegate to
    /// another. C#'s <c>WriteLine</c> overloads build exactly such shells internally.
    /// </summary>
    [Fact]
    public void A_variadic_function_can_forward_its_own_params()
    {
        Assert.Equal(6, Run(VariadicSum + """

            fn logged(params xs: int[]): int { return sum(xs); }
            fn main(): int { return logged(1, 2, 3); }
            """).AsI64);
    }

    [Fact]
    public void A_ready_made_array_is_passed_as_the_array_itself()
    {
        // Not as ONE element: 4+5+6, not 1, which would be the length of an array with one element.
        Assert.Equal(15, Run(VariadicSum + """

            fn main(): int { let a = [4, 5, 6]; return sum(a); }
            """).AsI64);
    }

    /// <summary>
    /// The unambiguity C# has to establish through overload resolution and Lyric through the type: with
    /// <c>params xs: int[][]</c> an element is <c>int[]</c> and the array is <c>int[][]</c>. Both calls
    /// yield 1, but for different reasons, and that is the point.
    /// </summary>
    [Theory]
    [InlineData("inner", 1)]           // int[] is one element, an array of length 1
    [InlineData("[inner, inner]", 2)]  // int[][] is the array itself, length 2
    public void The_argument_type_decides_element_versus_array(string argument, long expected) =>
        Assert.Equal(expected, Run($$"""
            fn count(params xs: int[][]): int { return xs.length; }

            fn main(): int {
                let inner = [1, 2];
                return count({{argument}});
            }
            """).AsI64);

    // ------------------------------------------------------------------ P5c: Konstanten

    [Fact]
    public void A_module_level_let_is_a_global_slot()
    {
        Assert.Equal(3, Run("""
            let pi = 3;
            fn main(): int { return pi; }
            """).AsI64);
    }

    [Fact]
    public void A_static_let_is_the_same_mechanism()
    {
        Assert.Equal(7, Run("""
            class V { static let Z: int = 7; }
            fn main(): int { return V.Z; }
            """).AsI64);
    }

    [Fact]
    public void An_initializer_may_read_an_earlier_constant()
    {
        Assert.Equal(5, Run("""
            let a = 2;
            let b = a + 3;
            fn main(): int { return b; }
            """).AsI64);
    }

    [Fact]
    public void A_function_reads_a_constant_declared_after_it()
    {
        // From a body every constant is readable wherever it stands: the init phase is long over by then.
        // The order applies INSIDE an initializer only.
        Assert.Equal(4, Run("""
            fn f(): int { return k; }
            let k = 4;
            fn main(): int { return f(); }
            """).AsI64);
    }

    [Fact]
    public void An_initializer_can_build_an_object()
    {
        // The case that makes it an init FUNCTION rather than values in the section: an initializer is an
        // expression, not a literal.
        Assert.Equal(9, Run("""
            class C { n: int }
            let cell = C { n = 9 };
            fn main(): int { return cell.n; }
            """).AsI64);
    }

    [Fact]
    public void The_documented_static_let_example_works()
    {
        // The case 'static let' was introduced for.
        Assert.Equal(50, Run("""
            class Enemy {
                name: string,
                hp: int,

                static let BASE_HP: int = 10;

                static fn new(level: int): Enemy {
                    return Enemy { name = "goblin", hp = Enemy.BASE_HP * level };
                }
            }

            fn main(): int { let e = Enemy.new(5); return e.hp; }
            """).AsI64);
    }

    // ------------------------------------------------------------------ P6: Closures

    [Fact]
    public void A_lambda_can_be_called_immediately() =>
        Assert.Equal(3, Run("fn main(): int { let f = (x: int) => x + 1; return f(2); }").AsI64);

    [Fact]
    public void A_captured_let_is_copied_into_the_environment() =>
        Assert.Equal(7, Run("""
            fn main(): int { let k = 5; let f = (x: int) => x + k; return f(2); }
            """).AsI64);

    [Fact]
    public void A_closure_outlives_the_call_that_made_it() =>
        // 'n' lives in a cell rather than in mk's frame; otherwise it would be gone here.
        Assert.Equal(2, Run("""
            fn mk(): fn() -> int { var n = 0; return (): int => { n += 1; return n; }; }
            fn main(): int { let c = mk(); c(); return c(); }
            """).AsI64);

    [Fact]
    public void Two_closures_share_one_captured_variable() =>
        // The counter-check to the test above: sharing means sharing. With two cells this would be 1.
        Assert.Equal(21, Run("""
            fn main(): int {
                var n = 1;
                let inc = (): int => { n += 10; return n; };
                let get = (): int => n;
                inc();
                inc();
                return get();
            }
            """).AsI64);

    [Fact]
    public void The_enclosing_function_sees_what_the_closure_wrote() =>
        // The other direction of the same cell; without this test "shared" would be half a statement.
        Assert.Equal(30, Run("""
            fn main(): int { var n = 0; let set = () => { n = 30; }; set(); return n; }
            """).AsI64);

    [Fact]
    public void A_closure_can_be_passed_as_an_argument() =>
        Assert.Equal(12, Run("""
            fn ap(f: fn(int) -> int, v: int): int { return f(v); }
            fn main(): int { let m = 3; return ap((x: int) => x * m, 4); }
            """).AsI64);

    [Fact]
    public void A_lambda_inside_a_lambda_reaches_the_outer_capture() =>
        // Nested: 'a' lies in the environment of the outer closure and the inner one reads it from there,
        // not from a slot that does not exist in its frame.
        Assert.Equal(8, Run("""
            fn main(): int { let a = 7; let f = (): int => { let g = (): int => a + 1; return g(); }; return f(); }
            """).AsI64);

    [Fact]
    public void A_closure_without_captures_needs_no_environment() =>
        // No newobj in the generated code: the value is a pure function index. The result is what is
        // measured; that no allocation happened is recorded by the disassembler.
        Assert.Equal(9, Run("""
            fn main(): int { let f = (x: int) => x * 3; return f(3); }
            """).AsI64);

    [Fact]
    public void An_array_of_function_values_runs()
    {
        // The case type parentheses were introduced for: without them this type could not be written down
        // although it existed.
        Assert.Equal(31, Run("""
            fn main(): int {
                let fs: (fn(int) -> int)[] = [(x: int) => x + 1, (x: int) => x * 2];
                return fs[0](10) + fs[1](10);
            }
            """).AsI64);
    }

    [Fact]
    public void A_parenthesized_type_is_the_type_itself() =>
        Assert.Equal(7, Run("fn main(): int { let a: (int) = 7; return a; }").AsI64);

    // ------------------------------------------------------------------ P7: Coroutinen

    /// <summary>Like <see cref="Run"/>, but with the stdlib: the jump table of a coroutine calls
    /// <c>std.core.coroutineEnded</c>, and only the module path binds that.</summary>
    private static LyrValue Coroutine(string source) => RunWithStdlib(source).Result;

    private static LyricPanic PanicFromCoroutine(string source) =>
        Assert.Throws<LyricPanic>(() => RunWithStdlib(source));

    [Fact]
    public void A_coroutine_resumes_where_it_left_off() =>
        // 'n' survives the 'yield'. Without preserved state this would yield 0 three times.
        Assert.Equal(2, Coroutine("""
            fn counter(): Coroutine<int> { var n = 0; while (true) { yield n; n += 1; } }
            fn main(): int { let c = counter(); resume c; resume c; return resume c; }
            """).AsI64);

    [Fact]
    public void Each_yield_is_its_own_resume_point() =>
        Assert.Equal(30, Coroutine("""
            fn three(): Coroutine<int> { yield 10; yield 20; yield 30; }
            fn main(): int { let t = three(); let a = resume t; let b = resume t; return a + b; }
            """).AsI64);

    [Fact]
    public void Two_coroutines_of_the_same_kind_have_separate_state() =>
        // The counter-check: the state hangs on the VALUE rather than on the function. With shared state
        // this would be 3.
        Assert.Equal(2, Coroutine("""
            fn counter(): Coroutine<int> { var n = 0; while (true) { yield n; n += 1; } }
            fn main(): int {
                let a = counter();
                let b = counter();
                resume a; resume a;
                resume b;
                return resume a;
            }
            """).AsI64);

    [Fact]
    public void A_coroutine_parameter_survives_the_first_yield() =>
        // Parameters live in the state object like every local; the factory writes them in on creation.
        Assert.Equal(14, Coroutine("""
            fn steps(by: int): Coroutine<int> { var n = 0; while (true) { yield n; n += by; } }
            fn main(): int { let s = steps(7); resume s; resume s; return resume s; }
            """).AsI64);

    [Fact]
    public void Resuming_a_finished_coroutine_is_an_error()
    {
        // The resume on which the body runs out has no value to deliver and says so.
        var panic = PanicFromCoroutine("""
            fn two(): Coroutine<int> { yield 1; yield 2; }
            fn main(): int { let c = two(); resume c; resume c; return resume c; }
            """);

        Assert.Contains("already finished", panic.Message);
    }

    // ------------------------------------------------------------------ P8: Generics

    [Fact]
    public void A_generic_function_runs() =>
        Assert.Equal(7, Run("""
            fn id<T>(x: T): T { return x; }
            fn main(): int { return id(7); }
            """).AsI64);

    [Fact]
    public void Two_type_arguments_do_not_interfere() =>
        Assert.Equal(5, Run("""
            fn id<T>(x: T): T { return x; }
            fn main(): int { let s = id("x"); return id(5); }
            """).AsI64);

    [Fact]
    public void A_generic_function_can_call_a_generic_function() =>
        // 'id' is requested from inside 'twice<int>', and which T is meant is known only to the calling
        // instance's substitution.
        Assert.Equal(4, Run("""
            fn id<T>(x: T): T { return x; }
            fn twice<T>(x: T): T { return id(id(x)); }
            fn main(): int { return twice(4); }
            """).AsI64);

    [Fact]
    public void A_generic_function_can_recurse() =>
        // The instance finds its own id already there, which is why it is assigned on request rather than
        // when lowering.
        Assert.Equal(3, Run("""
            fn down<T>(x: T, n: int): int { if (n <= 0) { return 0; } return 1 + down(x, n - 1); }
            fn main(): int { return down("a", 3); }
            """).AsI64);

    [Fact]
    public void A_generic_type_has_one_layout_per_instance() =>
        Assert.Equal(3, Run("""
            class Box<T> { v: T }
            fn main(): int { let a = Box<int> { v = 3 }; let s = Box<string> { v = "x" }; return a.v; }
            """).AsI64);

    [Fact]
    public void A_method_of_a_generic_type_is_instantiated_per_type() =>
        // The return type is T: it can come only from the INSTANCE, not from the definition.
        Assert.Equal(5, Run("""
            class Box<T> { v: T, fn get(): T { return this.v; } }
            fn main(): int { let a = Box<int> { v = 5 }; let s = Box<string> { v = "x" }; return a.get(); }
            """).AsI64);

    [Fact]
    public void A_generic_struct_lowers_like_any_other() =>
        // Generic AND value semantics: 'Pair<int>' goes through the same layout path as any other struct;
        // the instantiation changes nothing about that.
        Assert.Equal(5, Run("""
            struct Pair<T> { a: T, b: T, fn first(): T { return this.a; } }
            fn main(): int { let p = Pair<int> { a = 5, b = 3 }; return p.first(); }
            """).AsI64);

    [Fact]
    public void A_generic_interface_dispatches_dynamically() =>
        // The building block 'Iterator<T>' rests on: conformance to a generic interface, assignment to
        // its type, and a callvirt through it.
        Assert.Equal(7, Run("""
            interface Src<T> { fn next(): ?T; }
            class Ones :: [Src<int>] { fn next(): ?int { return 7; } }
            fn take(s: Src<int>): int { return s.next() ?? 0; }
            fn main(): int { let o = Ones { }; return take(o); }
            """).AsI64);

    // ------------------------------------------------------------------ P8c: for-in

    [Fact]
    public void For_in_walks_an_exclusive_range() =>
        Assert.Equal(6, Iterating("fn main(): int { var s = 0; for (n in 0..4) { s += n; } return s; }"));

    [Fact]
    public void For_in_walks_an_inclusive_range() =>
        // The inclusive range ends one later; the conversion happens while building the adapter, so there
        // is only ONE RangeIterator.
        Assert.Equal(10, Iterating("fn main(): int { var s = 0; for (n in 1..=4) { s += n; } return s; }"));

    [Fact]
    public void For_in_walks_an_array() =>
        Assert.Equal(33, Iterating("""
            fn main(): int { let xs = [10, 20, 3]; var s = 0; for (x in xs) { s += x; } return s; }
            """));

    [Fact]
    public void Break_and_continue_work_inside_for_in() =>
        // The loop is an ordinary LoopScope, so 'break' and 'continue' need no special case.
        Assert.Equal(8, Iterating("""
            fn main(): int {
                var s = 0;
                for (n in 0..5) { if (n == 2) { continue; } s += n; }
                return s;
            }
            """));

    [Fact]
    public void Two_loops_over_the_same_array_do_not_interfere() =>
        // The index belongs to the ITERATOR rather than to the array. With shared state this would be 6.
        Assert.Equal(12, Iterating("""
            fn main(): int {
                let xs = [1, 2, 3];
                var s = 0;
                for (a in xs) { s += a; }
                for (b in xs) { s += b; }
                return s;
            }
            """));

    [Fact]
    public void A_user_defined_iterator_is_used_directly() =>
        // No adapter: the type satisfies 'Iterator<T>' itself, so it is taken as it is.
        Assert.Equal(3, Iterating("""
            import std.iter { Iterator };
            class UpTo :: [Iterator<int>] {
                current: int,
                last: int,
                pub mut fn next(): ?int {
                    if (this.current > this.last) { return null; }
                    let v = this.current;
                    this.current = this.current + 1;
                    return v;
                }
            }
            fn main(): int {
                var n = 0;
                for (x in UpTo { current = 1, last = 2 }) { n += x; }
                return n;
            }
            """));

    /// <summary>'for-in' builds its iterator from std.iter; without a module path there is none.</summary>
    private static long Iterating(string source) => RunWithStdlib(source).Result.AsI64;

    // ------------------------------------------------------------------ P8: Constraints

    [Fact]
    public void A_constraint_dispatches_directly() =>
        // The gain of monomorphization: in the instance T is settled and so is the method. The dynamic
        // dispatch becomes a direct call — no callvirt, no vtable.
        Assert.Equal(4, Run("""
            interface P { fn price(): int; }
            class Item :: [P] { fn price(): int { return 4; } }
            fn total<T :: [P]>(x: T): int { return x.price(); }
            fn main(): int { return total(Item { }); }
            """).AsI64);

    [Fact]
    public void Each_constrained_instance_calls_its_own_implementation() =>
        // The counter-check: without separate instances the same method would run twice.
        Assert.Equal(8, Run("""
            interface P { fn price(): int; }
            class A :: [P] { fn price(): int { return 3; } }
            class B :: [P] { fn price(): int { return 5; } }
            fn total<T :: [P]>(x: T): int { return x.price(); }
            fn main(): int { return total(A { }) + total(B { }); }
            """).AsI64);

    [Fact]
    public void A_default_method_reached_through_a_constraint_goes_virtual() =>
        // A default method belongs to the INTERFACE and its 'this' is the interface type. No direct call
        // leads there: the receiver is lifted and callvirt does the rest.
        Assert.Equal(12, Run("""
            interface P { fn base(): int; fn twice(): int { return this.base() * 2; } }
            class Item :: [P] { fn base(): int { return 6; } }
            fn go<T :: [P]>(x: T): int { return x.twice(); }
            fn main(): int { return go(Item { }); }
            """).AsI64);

    [Fact]
    public void An_own_member_beats_the_default_through_a_constraint() =>
        // The counter-check to the test above: had the default won, this would be 99.
        Assert.Equal(3, Run("""
            interface P { fn base(): int; fn twice(): int { return 99; } }
            class Item :: [P] { fn base(): int { return 6; }, fn twice(): int { return 3; } }
            fn go<T :: [P]>(x: T): int { return x.twice(); }
            fn main(): int { return go(Item { }); }
            """).AsI64);

    [Fact]
    public void A_value_held_as_an_interface_still_dispatches_dynamically() =>
        // Two different questions, two paths: a constraint knows the type, an interface value does not.
        // This test holds that the second path has not been lost.
        Assert.Equal(7, Run("""
            interface P { fn price(): int; }
            class Item :: [P] { fn price(): int { return 7; } }
            fn main(): int { let p: P = Item { }; return p.price(); }
            """).AsI64);

    [Fact]
    public void A_match_statement_where_every_arm_returns() =>
        // The most common statement 'match' there is. The merge block arises only once an arm needs it;
        // created unconditionally it stays empty and is unreachable from the entry.
        Assert.Equal(7, Run("""
            enum E { A, B }
            fn main(): int { let e = E.A; match (e) { A => { return 7; }, B => { return 2; } } }
            """).AsI64);

    [Fact]
    public void A_match_statement_where_one_arm_falls_through() =>
        // The counter-check: when an arm falls through, the merge block MUST be there.
        Assert.Equal(3, Run("""
            fn main(): int {
                var r = 0;
                let n = 5;
                match (n) { 5 => { r = 3; }, _ => { return 0; } }
                return r;
            }
            """).AsI64);

    // ------------------------------------------------------------------ Tupel (§4)

    [Fact]
    public void A_tuple_can_be_destructured() =>
        Assert.Equal(3, Run("fn main(): int { let (a, b) = (1, 2); return a + b; }").AsI64);

    [Fact]
    public void A_wildcard_binds_nothing() =>
        // '_' does not read the field at all: an ldfld without a consumer would be dead code.
        Assert.Equal(7, Run("fn main(): int { let (a, _) = (7, 2); return a; }").AsI64);

    [Fact]
    public void Tuple_patterns_nest() =>
        Assert.Equal(6, Run("""
            fn main(): int { let (a, (b, c)) = (1, (2, 3)); return a + b + c; }
            """).AsI64);

    [Fact]
    public void A_tuple_is_a_return_type() =>
        // The case tuples exist for: returning several values without declaring a type for it.
        // erfinden.
        Assert.Equal(12, Run("""
            fn pair(): (int, int) { return (3, 4); }
            fn main(): int { let (a, b) = pair(); return a * b; }
            """).AsI64);

    [Fact]
    public void The_initializer_runs_once() =>
        // 'let (a, b) = f();' must NOT call f twice. The counter lives in a cell, because a global 'var'
        // is not allowed; with two calls this would be 2.
        Assert.Equal(1, Run("""
            fn main(): int {
                var calls = 0;
                let count = (): (int, int) => { calls += 1; return (0, 0); };
                let (a, b) = count();
                return calls;
            }
            """).AsI64);

    [Fact]
    public void A_match_takes_a_tuple_apart() =>
        // The same pattern as in destructuring, and therefore the same routine in the lowering.
        Assert.Equal(3, Run("""
            fn main(): int { let t = (1, 2); return match (t) { (a, b) => a + b }; }
            """).AsI64);

    [Fact]
    public void A_var_destructuring_binds_mutable_names() =>
        Assert.Equal(7, Run("""
            fn main(): int { var (a, b) = (1, 2); a = 5; return a + b; }
            """).AsI64);

    [Fact]
    public void Tuples_of_the_same_shape_share_one_layout() =>
        // Interned: two '(int, int)' are the same table entry. Otherwise the type table would grow with
        // the number of LITERALS rather than with the number of shapes.
        Assert.Equal(10, Run("""
            fn main(): int {
                let (a, b) = (1, 2);
                let (c, d) = (3, 4);
                return a + b + c + d;
            }
            """).AsI64);

    // ------------------------------------------------------- globals with a composite type

    /// <summary>
    /// A module <c>let</c> may have any type a local variable may have.
    ///
    /// <para>A section of its own because it did not work: a global of type <c>T[]</c> or <c>?T</c> broke
    /// the lowering with "type not lowerable" — as a crash with a stack trace rather than a diagnostic —
    /// while the same expression compiled inside a function. The cause was a second, incomplete copy of
    /// the mapping from sema type to IR type; it is deleted, and these tests hold what it concealed.
    /// </para>
    /// </summary>
    [Fact]
    public void A_global_may_be_an_array()
    {
        Assert.Equal(5, Run("""
            let primes = [2, 3, 5, 7];
            fn main(): int { return primes[2]; }
            """).AsI64);
    }

    [Fact]
    public void A_global_array_is_writable_and_knows_its_length()
    {
        Assert.Equal(15, Run("""
            let xs = [1, 2, 3];
            fn main(): int {
                xs[0] = 12;
                return xs[0] + xs.length;
            }
            """).AsI64);
    }

    [Fact]
    public void A_global_may_be_optional()
    {
        Assert.Equal(7, Run("""
            let maybe: ?int = 7;
            fn main(): int { return maybe ?? 0; }
            """).AsI64);
    }

    [Fact]
    public void A_global_may_be_an_array_of_strings()
    {
        // A reference type in the element, so not only the scalar path is checked.
        Assert.Equal(5, Run("""
            let names = ["ada", "grace"];
            fn main(): int {
                if (names[1] == "grace") { return 5; }
                return 0;
            }
            """).AsI64);
    }

    // ------------------------------------------------------------- char as a number

    /// <summary>
    /// <c>char</c> counts as numeric: comparisons, casts and arithmetic work.
    ///
    /// <para>The occasion is <c>std.string</c> — "is this a digit?" is code point arithmetic, and without
    /// a way there every such function would have to be native.</para>
    /// </summary>
    [Fact]
    public void A_char_compares_and_casts_like_a_number()
    {
        Assert.Equal(97, Run("fn main(): int { let c = 'a'; return c as int; }").AsI64);
        Assert.Equal(98, Run("fn main(): int { return ('b' as int); }").AsI64);
        Assert.Equal(1, Run("fn main(): int { return if ('a' < 'z') 1 else 0; }").AsI64);
    }

    [Fact]
    public void An_untyped_literal_adapts_to_char()
    {
        // 'c + 1' — the literal IS a char, it is not converted. Without the adaptation in UnifyNumeric a
        // 'const i64' stood next to a char operand and the IR verifier made the compiler CRASH instead of
        // diagnosing.
        Assert.Equal(98, Run("fn main(): int { let c = 'a'; return (c + 1) as int; }").AsI64);
    }

    [Fact]
    public void The_highest_codepoint_is_valid() =>
        // The bound itself has to pass, or the test below only checks that something gets rejected.
        Assert.Equal(1114111, Run("fn main(): int { return (1114111 as char) as int; }").AsI64);

    [Fact]
    public void A_codepoint_beyond_the_range_panics()
    {
        var panic = RunExpectingPanic("fn main(): int { return (1114112 as char) as int; }");

        Assert.Equal(VmDiagnostics.InvalidCodepoint, panic.Code);
    }

    [Fact]
    public void A_surrogate_is_not_a_char() =>
        // D800 lies BELOW the upper bound and is still not a character: it is half of a UTF-16 pair.
        // Without this test the check would stay green if it only knew the upper bound.
        Assert.Equal(VmDiagnostics.InvalidCodepoint,
            RunExpectingPanic("fn main(): int { return (55296 as char) as int; }").Code);

    [Fact]
    public void Arithmetic_that_leaves_the_range_panics() =>
        // Checked on CREATION rather than on use: the value is never printed here.
        Assert.Equal(VmDiagnostics.InvalidCodepoint,
            RunExpectingPanic("fn main(): int { let c = 'a'; let d = c * 1000000; return 0; }").Code);
}
