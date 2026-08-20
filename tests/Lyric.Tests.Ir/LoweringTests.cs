using System.Runtime.CompilerServices;
using System.Text;
using Lyric.Core;
using Lyric.Ir;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Ir;

/// <summary>
/// Tests for the AST to IR lowering.
///
/// <para>GOLDEN TESTS ARE THE BACKBONE: source in, IR dump out, compared against a snapshot. Source
/// and expectation lie as a pair in <c>golden/lowering/&lt;name&gt;.lyr</c> and <c>.ir</c>, the same
/// pattern as the lexer goldens.</para>
///
/// <para>THE VERIFIER RUNS IN EVERY ONE OF THESE TESTS. <see cref="ModuleLowerer.Lower"/> calls
/// <see cref="IrVerifier.VerifyOrThrow"/>, so a finding throws before anything is compared. The
/// verifier test cases are therefore sharp against real lowering rather than only against hand-built
/// fixtures.</para>
///
/// <para>The unit tests below pin down the invariants one can see in the dump but easily overlook:
/// block density, the parameter convention, discarded dead code.</para>
/// </summary>
public class LoweringTests
{
    // ------------------------------------------------------------------ helpers

    /// <summary>Source to IR. Stops when the sema complains: on a faulty AST every lowering result would
    /// be guesswork.</summary>
    private static IrModule Lower(string source, bool verify = true)
    {
        var (ir, de) = TryLower(source, verify);
        Assert.True(ir is not null, "lowering reported diagnostics:\n" + Render(de));
        return ir!;
    }

    /// <summary>Like <see cref="Lower"/>, but accepts reported scope boundaries. Still stops on sema
    /// errors: on a faulty AST every lowering result would be guesswork.</summary>
    private static (IrModule? Ir, DiagnosticEngine De) TryLower(string source, bool verify = true)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        Assert.False(de.HasErrors, "source did not type-check:\n" + Render(de));
        // optimize:false — these tests pin the SHAPE of lowered code. The inliner would fold the
        // very call or instance a test asserts; it has tests of its own.
        return (ModuleLowerer.Lower(comp, binding, types, de, verify, optimize: false), de);
    }

    private static string Render(DiagnosticEngine de)
    {
        var writer = new StringWriter();
        de.RenderText(writer);
        return writer.ToString();
    }

    private static IrFunction Single(string source) => Assert.Single(Lower(source).Functions);

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string GoldenDir([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "golden", "lowering");

    // ------------------------------------------------------------------ 1) Golden

    private static bool UpdateMode =>
        Environment.GetEnvironmentVariable("LYRIC_UPDATE_SNAPSHOTS") is "1" or "true";

    [Theory]
    [InlineData("arith")]           // parameters, binop, ret — the scaffolding
    [InlineData("if_else")]         // both branches fall through, so there is a merge block
    [InlineData("if_both_return")]  // both branches return, so there is NO merge block
    [InlineData("if_no_else")]      // without an else the false branch is the merge block
    [InlineData("while_loop")]      // a back edge, break, continue, nested ifs
    [InlineData("do_while")]        // continue jumps to the condition, not to the start of the body
    [InlineData("if_expr")]         // if as an expression through a synthetic local
    [InlineData("short_circuit")]   // && and || as control flow
    [InlineData("calls")]           // a void call, a forward call, recursion
    [InlineData("cast")]            // convert plus an elided identity
    [InlineData("incdec")]          // ++ and -- in prefix and postfix, compound assignment
    [InlineData("objects")]         // newobj, reading and writing a field, reference semantics
    [InlineData("objects_nested")]  // a class as a field type, plus a recursive type
    [InlineData("methods")]         // the receiver as parameter 0, a static factory, 'this'
    [InlineData("arrays")]          // Literal, [x]*n, xs+ys, Index lesend/schreibend, .length
    [InlineData("optionals")]       // null, ??, !, flow narrowing
    [InlineData("enums")]           // variants, match, tag dispatch, pattern decomposition
    [InlineData("interfaces")]      // mkiface, callvirt, vtable rows, default against override
    [InlineData("structs")]         // structcopy at the binding points, a nested value type
    public void Golden_lowering_matches_snapshot(string name)
    {
        var dir = GoldenDir();
        var sourcePath = Path.Combine(dir, name + ".lyr");
        var snapshotPath = Path.Combine(dir, name + ".ir");

        Assert.True(File.Exists(sourcePath), $"missing source fixture: {sourcePath}");
        var actual = Normalize(IrPrinter.Dump(Lower(File.ReadAllText(sourcePath, Encoding.UTF8))));

        if (UpdateMode)
        {
            File.WriteAllText(snapshotPath, actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(snapshotPath),
            $"missing snapshot: {snapshotPath}\n" +
            "Run once with LYRIC_UPDATE_SNAPSHOTS=1 to generate it, then review and commit.");

        Assert.Equal(Normalize(File.ReadAllText(snapshotPath, Encoding.UTF8)), actual);
    }

    [Fact]
    public void Every_fixture_lowers_to_verifier_clean_ir()
    {
        // Lowered explicitly with verify:false and checked afterwards; otherwise the test would only
        // repeat that ModuleLowerer calls the verifier rather than show its result.
        foreach (var path in Directory.GetFiles(GoldenDir(), "*.lyr"))
        {
            var module = Lower(File.ReadAllText(path, Encoding.UTF8), verify: false);
            var findings = IrVerifier.Verify(module);
            Assert.True(findings.Count == 0,
                $"{Path.GetFileName(path)} produced malformed IR:\n  " + string.Join("\n  ", findings));
        }
    }

    [Fact]
    public void Gate_program_lowers_end_to_end()
    {
        // examples/arith.lyr is deliberately stdlib-free, so it compiles from the core alone.
        var path = Path.Combine(RepoRoot(), "examples", "arith.lyr");
        Assert.True(File.Exists(path), $"missing gate program: {path}");

        var module = Lower(File.ReadAllText(path, Encoding.UTF8));

        Assert.Equal(6, module.Functions.Count);
        Assert.Contains(module.Functions, f => f.Name == "main.main");
        Assert.Empty(IrVerifier.Verify(module));
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    // ------------------------------------------------------------- 1b) Source-first Stdlib

    /// <summary>Like <see cref="TryLower"/>, but with the real stdlib on the module path: it is ordinary
    /// Lyric source and is loaded while resolving.</summary>
    private static IrModule LowerWithStdlib(string source)
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

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true, optimize: false);
        Assert.NotNull(ir);
        return ir!;
    }

    [Fact]
    public void Stdlib_is_loaded_from_source_when_imported()
    {
        // The core of source-first: std.io.console is an ordinary Lyric module, loaded and type-checked
        // while resolving, with no special case in the compiler. Since v1.14 'println' is a Lyric
        // body over Display; the import it reaches is the private native behind it.
        var module = LowerWithStdlib("""
            import std.io.console { println };
            fn main(): int { println("hi"); return 0; }
            """);

        var println = Assert.Single(module.Imports, i => i.Name == "std.io.console.rawPrintln");
        Assert.Equal(new IrScalarType(IrScalar.String), Assert.Single(println.ParamTypes));
        Assert.Equal(new IrScalarType(IrScalar.Void), println.ReturnType);
    }

    /// <summary>
    /// A program carries ONLY what it really needs.
    ///
    /// <para>Measured on this hello world: from 420 bytes with four imports and four functions down to
    /// 230 bytes with one import and one function — 9 bytes of code, exactly <c>main</c>. Since v1.14
    /// the hello world carries THREE functions: <c>main</c>, the monomorphized
    /// <c>println&lt;string&gt;</c> wrapper over the private native, and the
    /// <c>extend string</c> <c>show</c> it dispatches through. That is the print family's
    /// generality as two small Lyric bodies (this test lowers UNOPTIMIZED; the inliner exists
    /// for exactly this shape), not a return of the old bloat.</para>
    /// </summary>
    [Fact]
    public void A_program_carries_only_what_it_reaches()
    {
        var module = LowerWithStdlib("""
            import std.io.console { println };
            fn main(): int { println("hi"); return 0; }
            """);

        Assert.Equal(3, module.Functions.Count);
        Assert.Single(module.Imports);
        Assert.Equal("std.io.console.rawPrintln", module.Imports[0].Name);

        // The three that used to come along unavoidably.
        var namen = module.Functions.Select(f => f.Name).ToArray();
        Assert.DoesNotContain("std.iter.RangeIterator.next", namen);
        Assert.DoesNotContain("std.iter.StringIterator.next", namen);
        Assert.DoesNotContain("std.io.console.prompt", namen);
    }

    /// <summary>
    /// The canonical <c>@Deprecated</c> emits no attribute row and roots nothing: the promise is
    /// that it changes diagnostics and NOTHING else. With a row, the pruner would keep every
    /// deprecated declaration alive in every importing program — dead code carried around exactly
    /// because it was marked for removal.
    /// </summary>
    [Fact]
    public void A_deprecated_declaration_is_neither_a_row_nor_a_root()
    {
        // Since 2.0 the stdlib carries no @Deprecated itself, so the fixture brings its own:
        // an uncalled deprecated function must not survive into the module, and no attribute
        // row may exist at all.
        var module = LowerWithStdlib("""
            import std.core { Deprecated };

            @Deprecated { message = "old" }
            fn veraltet(): int { return 1; }

            fn main(): int {
                return 0;
            }
            """);

        Assert.DoesNotContain(module.Functions, f => f.Name.Contains("veraltet"));
        Assert.Empty(module.Attributes);
    }

    [Fact]
    public void Stdlib_signatures_are_enforced()
    {
        // The proof that the signature really arrives: with every stdlib symbol opaque, a wrong
        // argument type would pass silently. `println(42)` stopped being the example when the
        // print family went generic over Display in v1.14; `writeText` keeps two fixed string
        // parameters, so a number in the second slot is the same proof.
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr",
            "import std.io.file { writeText };\nfn main(): int { let _ = writeText(\"p\", 42); return 0; }");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);

        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0001");
    }

    [Fact]
    public void Interpolation_lowers_to_a_concat_chain()
    {
        var module = LowerWithStdlib("""
            fn main(): int { let n = 7; let s = f"n={n}!"; return 0; }
            """);

        // "n=" ++ fromInt(n) ++ "!" — two concats, one converter
        //
        // CONTAINMENT is checked rather than exclusivity. The Lyric bodies in 'std.string' (parseInt,
        // replace, …) drag their own natives along as soon as the module is loaded, even when nobody
        // calls them.
        var namen = module.Imports.Select(i => i.Name).ToArray();
        Assert.Contains("std.string.fromInt", namen);
        Assert.Contains("std.string.concat", namen);
    }

    [Fact]
    public void Adjacent_text_segments_collapse_into_one_constant()
    {
        // f"ab" has no hole: the result is a plain constant rather than a concat.
        //
        // What the test really says is: NO converter and NO concat.
        var module = LowerWithStdlib("fn main(): int { let s = f\"ab\"; return 0; }");
        var namen = module.Imports.Select(i => i.Name).ToArray();
        Assert.DoesNotContain("std.string.concat", namen);
        Assert.DoesNotContain("std.string.fromInt", namen);
    }

    [Fact]
    public void A_bodyless_function_outside_the_stdlib_is_an_error()
    {
        // Exactly the mechanism the stdlib uses; in user code it has to be closed, or anyone could
        // declare arbitrary natives.
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", "fn native(x: int): int;\nfn main(): int { return 0; }");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);

        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0051");
    }

    // ------------------------------------------------------------------ 2) Invarianten

    [Fact]
    public void Block_ids_are_dense_and_entry_is_the_first_block()
    {
        var fn = Single("""
            fn f(limit: int): int {
                var acc = 0;
                var n = limit;
                while (n > 0) {
                    if (n % 2 == 0) { acc += n; } else { acc -= n; }
                    n -= 1;
                }
                return acc;
            }
            """);

        for (var i = 0; i < fn.Blocks.Count; i++)
            Assert.Equal(i, fn.Blocks[i].Id.Value);
        Assert.Equal(fn.Blocks[0].Id, fn.Entry);
        Assert.True(fn.Blocks.Count > 4, "fixture should produce a non-trivial CFG");
    }

    [Fact]
    public void First_locals_are_the_parameters_in_order()
    {
        var fn = Single("fn f(alpha: int, beta: bool): int { let gamma = 1; return alpha + gamma; }");

        Assert.Equal(2, fn.ParamCount);
        Assert.Equal("alpha", fn.Locals[0].Name);
        Assert.Equal("beta", fn.Locals[1].Name);
        Assert.Equal("gamma", fn.Locals[2].Name);
    }

    [Fact]
    public void Statements_after_a_return_are_dropped()
    {
        // A block for the dead code would be unreachable, and the verifier rejects unreachable blocks.
        // The lowering has to stop the statement list rather than clean up afterwards.
        var fn = Single("fn f(): int { return 1; let dead = 2; }");

        Assert.Single(fn.Blocks);
        Assert.DoesNotContain(fn.Locals, l => l.Name == "dead");
    }

    [Fact]
    public void If_with_both_arms_returning_creates_no_merge_block()
    {
        var fn = Single("fn f(n: int): int { if (n > 0) { return 1; } else { return 0; } }");

        // bb0 is the condition, bb1 the then, bb2 the else. A fourth block would be the unreachable merge.
        Assert.Equal(3, fn.Blocks.Count);
    }

    [Fact]
    public void Void_function_gets_an_implicit_return()
    {
        var fn = Single("fn f(n: int) { var x = n; x += 1; }");

        var terminator = Assert.IsType<Return>(fn.Blocks[^1].Terminator);
        Assert.Null(terminator.Value);
    }

    [Fact]
    public void Identity_cast_is_elided()
    {
        var fn = Single("fn f(x: int): int { return x as int; }");

        Assert.DoesNotContain(fn.Blocks.SelectMany(b => b.Insts), op => op is Lyric.Ir.Convert);
    }

    [Fact]
    public void Widening_cast_emits_a_convert()
    {
        var fn = Single("fn f(x: int32): int64 { return x as int64; }");

        var convert = Assert.Single(fn.Blocks.SelectMany(b => b.Insts).OfType<Lyric.Ir.Convert>());
        Assert.Equal(new IrScalarType(IrScalar.I32), convert.From);
        Assert.Equal(new IrScalarType(IrScalar.I64), convert.To);
    }

    [Fact]
    public void Float32_literal_is_narrowed_by_the_lowering()
    {
        // An f32 const whose value is no f32 value would be malformed. The narrowing belongs in the
        // lowering, so the value in the bytecode is deterministically the same.
        var fn = Single("fn f(): float32 { return 0.1f32; }");

        var constant = Assert.Single(fn.Blocks.SelectMany(b => b.Insts).OfType<Const>());
        var value = Assert.IsType<FloatConst>(constant.Value);
        Assert.Equal((double)(float)0.1, value.Value);
    }

    [Fact]
    public void Short_circuit_routes_the_value_through_a_synthetic_local()
    {
        // A temp may be defined only once and therefore cannot carry the value from two branches. That is
        // exactly why this IR needs no phi.
        var fn = Single("fn f(a: bool, b: bool): bool { return a && b; }");

        Assert.Contains(fn.Locals, l => l.Name.StartsWith("$and", StringComparison.Ordinal));
    }

    [Fact]
    public void Recursion_and_forward_calls_resolve()
    {
        var module = Lower("""
            fn fact(n: int): int {
                if (n <= 1) { return 1; }
                return n * fact(n - 1);
            }
            fn main(): int { return helper(); }
            fn helper(): int { return fact(3); }
            """);

        var calls = module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Insts)
            .OfType<Call>().ToList();
        Assert.Equal(3, calls.Count);
        Assert.All(calls, c => Assert.InRange(c.Target.Value, 0, module.Functions.Count - 1));
    }

    [Fact]
    public void Short_circuit_inside_a_loop_condition_seals_the_right_block()
    {
        // '&&' produces blocks itself, so after the condition the cursor no longer stands on the cond
        // block. Sealing the cond block rather than the current one builds a jump into nothing.
        var fn = Single("""
            fn f(a: int, b: int): int {
                var x = a;
                var y = b;
                while (x > 0 && y > 0) {
                    x -= 1;
                    y -= 1;
                }
                return x;
            }
            """);

        Assert.All(fn.Blocks, b => Assert.NotNull(b.Terminator));
    }

    [Fact]
    public void Continue_in_a_do_while_jumps_to_the_condition()
    {
        // Not to the start of the body: 'do' checks at the end, and a continue has to land there, or the
        // condition is skipped.
        var fn = Single("""
            fn f(n: int): int {
                var i = n;
                var seen = 0;
                do {
                    i -= 1;
                    if (i % 2 == 0) { continue; }
                    seen += 1;
                } while (i > 0);
                return seen;
            }
            """);

        // Exactly one block is targeted by two branches: the regular end of the body and the continue.
        // That is the condition of the do-while, recognisable by its CondBranch.
        var shared = fn.Blocks.Select(b => b.Terminator).OfType<Branch>()
            .GroupBy(br => br.Target)
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key)
            .ToList();

        var target = Assert.Single(shared);
        Assert.IsType<CondBranch>(fn.Blocks[target.Value].Terminator);
    }

    [Fact]
    public void Lowering_is_deterministic()
    {
        const string source = """
            fn f(a: int, b: int): int {
                var acc = 0;
                if (a > b && a > 0) { acc = a; } else { acc = b; }
                while (acc > 0) { acc -= 1; }
                return if (acc == 0) 1 else 0;
            }
            """;

        Assert.Equal(IrPrinter.Dump(Lower(source)), IrPrinter.Dump(Lower(source)));
    }

    // ------------------------------------------------------------------ 3) scope boundaries

    // The rule still holds: a type boundary is reported at the TYPE rather than at the expression using
    // it. There is no type left that the sema accepts and the lowering rejects.

    // Constructs whose type is scalar: here the boundary applies at the expression or statement.
    [Theory]
    // f-strings lower to a concat and fromXxx chain. Without the stdlib on the module path the helpers
    // are missing, and the message names the missing one rather than claiming "f-strings do not work".
    // At the first hole that is the converter, before the concat.
    [InlineData("fn f(): string { return f\"n={1}\"; }", "std.string.fromInt")]
    // 'match' over an enum lowers; over a scalar it needs literal patterns, which are a later stage.
    // 'for-in' lowers with std.iter on the module path, and these tests run without it. The message
    // names the reason rather than merely the construct, and that is what this test checks: that it says
    // WHERE and WHAT.
    [InlineData("fn f(): int { var s = 0; for (i in 0..3) { s += i; } return s; }", "std.iter")]
    public void Out_of_scope_constructs_report_where_and_what(string source, string expected) =>
        AssertNotSupported(source, expected);

    /// <summary>
    /// The M24 probe result, pinned: a GENERIC default method on an interface is sema-legal and
    /// refused by the lowering — a default body with its own type parameters has no
    /// monomorphization path yet, and an interface value's instance interning cannot carry the
    /// <c>fn(T) -&gt; U</c> slot signature either. This is why iterator method chaining is a
    /// documented No (STATUS §Design decisions): both walls are milestone-sized. The pin keeps
    /// today's refusal a visible decision instead of an accident.
    /// </summary>
    [Fact]
    public void A_generic_interface_default_is_refused_by_the_lowering_not_the_sema()
    {
        var (ir, de) = TryLower("""
            interface Producer {
                mut fn next(): ?int;

                fn firstMapped<U>(f: fn(int) -> U): ?U {
                    let v = this.next();
                    if (v == null) {
                        return null;
                    }
                    return f(v);
                }
            }

            class Counter :: [Producer] {
                current: int,

                pub mut fn next(): ?int {
                    this.current = this.current + 1;
                    return this.current;
                }
            }

            fn f(): int {
                let c = Counter { current = 0 };
                return c.firstMapped<int>((n: int) => n * 10) ?? -1;
            }
            """);

        Assert.Null(ir);
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-IR0001");
    }

    /// <summary>What the lowering does not handle is valid Lyric, so it is a DIAGNOSTIC with file, line
    /// and column rather than a crash. The code <c>LYR-IR0001</c> is the stable category — "this compiler
    /// build cannot do that yet" — and the construct stands in the message.</summary>
    private static void AssertNotSupported(string source, string expected)
    {
        var (ir, de) = TryLower(source);

        Assert.Null(ir); // no partial result: the FunctionIds would be shifted
        var diagnostic = Assert.Single(de.Diagnostics);
        Assert.Equal("LYR-IR0001", diagnostic.Code);
        Assert.Equal(Severity.Error, diagnostic.Severity);
        Assert.Contains(expected, diagnostic.Message, StringComparison.Ordinal);

        // The span points into the source file.
        Assert.True(diagnostic.Span.File.IsValid, "diagnostic has no source position");
        Assert.Contains("test.lyr:", Render(de), StringComparison.Ordinal);
    }

    /// <summary>
    /// A type whose layout fails at a scope boundary must not corrupt the type table.
    ///
    /// <para>That was a compiler crash rather than a blemish: <c>Intern</c> records the placeholder
    /// BEFORE it lowers the field types. If it threw afterwards, the placeholder stayed and the next
    /// function using the same type read a layout with <c>FieldNames == null</c>.</para>
    ///
    /// <para>Two functions are required: with only one the second access never happens.</para>
    /// </summary>
    // What it secured stays right and untested: a failed layout is reported ONCE rather than per
    // function, and the placeholder in the type table is not read. Should such a type ever appear again,
    // the test belongs back.

    [Fact]
    public void A_generic_call_becomes_an_instance_of_its_own()
    {
        // Monomorphization: one function per concrete type argument tuple. The declaration itself gets
        // NONE — it is a template, not code.
        var (ir, de) = TryLower("""
            fn id<T>(x: T): T { return x; }
            fn main(): int { return id(1); }
            """);

        Assert.False(de.HasErrors);
        Assert.NotNull(ir);

        // The name carries the type arguments: it is the key the instance is found again by, and it is
        // readable in a disassembly.
        Assert.Contains(ir!.Functions, f => f.Name == "id<int>");
        Assert.DoesNotContain(ir.Functions, f => f.Name == "main.id");
    }

    [Fact]
    public void Two_type_arguments_produce_two_instances()
    {
        // The counter-check: without it the test above would only prove that SOME instance arises, not
        // that they are separate per type.
        var (ir, de) = TryLower("""
            fn id<T>(x: T): T { return x; }
            fn main(): int { let s = id("x"); return id(1); }
            """);

        Assert.False(de.HasErrors);
        Assert.Contains(ir!.Functions, f => f.Name == "id<int>");
        Assert.Contains(ir.Functions, f => f.Name == "id<string>");
    }

    [Fact]
    public void The_same_type_argument_is_instantiated_once()
    {
        // Two calls with the same type share one instance; otherwise the bytecode would grow with the
        // number of CALLS rather than with the number of types.
        var (ir, de) = TryLower("""
            fn id<T>(x: T): T { return x; }
            fn main(): int { return id(1) + id(2); }
            """);

        Assert.False(de.HasErrors);
        Assert.Single(ir!.Functions, f => f.Name == "id<int>");
    }

    [Fact]
    public void All_scope_limits_of_a_program_are_reported_in_one_run()
    {
        // One message per call would be harassment: whoever uses three unsupported constructs should see
        // them in one run, so the lowering keeps collecting per function. The test measures at whichever
        // boundary still stands; it counts messages, it does not claim which constructs are missing.
        var (ir, de) = TryLower("""
            fn a(): int { var s = 0; for (i in 0..4) { s += i; } return s; }
            fn b(): int { var s = 0; for (i in 0..3) { s += i; } return s; }
            fn c(): int { var s = 0; for (i in 0..2) { s += i; } return s; }
            """);

        Assert.Null(ir);
        Assert.Equal(3, de.Diagnostics.Count);
        Assert.All(de.Diagnostics, d => Assert.Equal("LYR-IR0001", d.Code));
    }

    [Fact]
    public void Generic_function_alone_lowers_to_an_empty_module()
    {
        Assert.Empty(Lower("fn id<T>(x: T): T { return x; }").Functions);
    }

    /// <summary>
    /// <c>fn main(args: string[])</c> is specified and has to say so.
    ///
    /// <para>Falling through the entry condition leaves the module without a Start section and the
    /// compiler reporting nothing. A program that compiles cleanly and then does not start, as a
    /// "library", is the worst of all answers.</para>
    /// </summary>
    [Fact]
    public void Main_with_arguments_is_an_entry_point()
    {
        // The second entry form of the specification.
        var (ir, de) = TryLower("fn main(args: string[]): int { return args.length; }");

        Assert.False(de.HasErrors);
        Assert.NotNull(ir!.EntryFunction);
        Assert.Equal(1, ir.Functions[ir.EntryFunction!.Value.Value].ParamCount);
    }

    // The counter-check — 'fn main(n: int)' — lives in the sema suite: it catches this with LYR-SEM0021
    // and the lowering never sees it. The fallback there is defence in depth and stays untested, because
    // it is unreachable.

    [Fact]
    public void A_module_without_a_main_is_a_library()
    {
        // No error: embedded code has no 'main'; the host calls individual functions.
        var (ir, de) = TryLower("pub fn onStart(): int { return 0; }");

        Assert.False(de.HasErrors);
        Assert.NotNull(ir);
        Assert.Null(ir!.EntryFunction);
    }
}
