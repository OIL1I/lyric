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
/// Static members on an enum and through an extend block, end to end.
///
/// <para>The parser was the only thing missing for an enum: the sema and the lowering carried the
/// case already. An extend block needed one more place — static lookup consulted the type's own
/// members only, while the instance path had consulted the extension registry all along. The
/// lowering was ready for both (<c>decl.IsStatic ? null : owner.Target</c>).</para>
///
/// <para>Run rather than checked: a static member that type-checks and then reaches the wrong
/// function, or gets a receiver it must not have, only shows in the result.</para>
/// </summary>
public class StaticMemberTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static (Compilation Comp, DiagnosticEngine Diagnostics, SourceManager Sources) Front(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de)
        {
            ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de),
        };
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        return (comp, de, sm);
    }

    private static long Run(string source)
    {
        var (comp, de, _) = Front(source);
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

    /// <summary>The diagnostic codes of a program that must not compile.</summary>
    private static string[] Errors(string source)
    {
        var (comp, de, _) = Front(source);
        var binding = comp.Resolve();
        Semantics.Analyze(comp, binding, de);
        Assert.True(de.HasErrors, "the program compiled, but the test expects it to be refused");
        return de.Diagnostics.Select(d => d.Code).ToArray();
    }

    // ------------------------------------------------- statics of a generic type, generically

    [Fact]
    public void A_generic_static_call_with_a_concrete_instance_runs()
    {
        Assert.Equal(7, Run("""
            class Box<T> {
                item: ?T,

                pub static fn empty(): Box<T> {
                    let hole: ?T = null;
                    return Box<T> { item = hole };
                }
            }
            fn main(): int {
                let b = Box<int>.empty();
                return if (b.item == null) 7 else 0;
            }
            """));
    }

    [Fact]
    public void A_generic_static_call_inside_a_generic_function_substitutes_the_callers_T()
    {
        // The regression the standard library's constructor rework found: in 'make<T>' the call
        // 'Box<T>.empty()' names the CALLER's T, and the static dispatch handed the instance to
        // the monomorphizer unsubstituted — the type table then met a bare parameter and threw.
        Assert.Equal(9, Run("""
            class Box<T> {
                item: ?T,

                pub static fn empty(): Box<T> {
                    let hole: ?T = null;
                    return Box<T> { item = hole };
                }
            }
            fn make<T>(): Box<T> {
                return Box<T>.empty();
            }
            fn main(): int {
                let b = make<int>();
                return if (b.item == null) 9 else 0;
            }
            """));
    }

    // ------------------------------------------------------------------ enum

    [Fact]
    public void A_static_factory_on_an_enum_returns_a_value()
    {
        Assert.Equal(7, Run("""
            enum Shape {
                Circle(float);
                static fn unit(): Shape { return Shape.Circle(1.0); }
            }
            fn main(): int {
                let s = Shape.unit();
                return match (s) { Shape.Circle(r) => 7, };
            }
            """));
    }

    [Fact]
    public void A_static_enum_method_stands_beside_an_instance_method()
    {
        // The receiver convention is the thing under test: 'tag' takes one, 'first' must not.
        Assert.Equal(2, Run("""
            enum E {
                A, B;
                fn tag(): int { return match (this) { E.A => 2, E.B => 5, }; }
                static fn first(): E { return E.A; }
            }
            fn main(): int { return E.first().tag(); }
            """));
    }

    [Fact]
    public void A_static_enum_method_takes_arguments()
    {
        Assert.Equal(9, Run("""
            enum E {
                N, V(int);
                static fn of(n: int): E { return E.V(n); }
            }
            fn main(): int { return match (E.of(9)) { E.V(n) => n, E.N => 0, }; }
            """));
    }

    [Fact]
    public void A_static_method_on_a_generic_enum_carries_its_type_argument()
    {
        Assert.Equal(4, Run("""
            enum O<T> {
                N, S(T);
                static fn none(): O<T> { return O.N; }
            }
            fn main(): int {
                let o: O<int> = O<int>.none();
                return match (o) { O.S(v) => v, O.N => 4, };
            }
            """));
    }

    // ------------------------------------------------------------------ extend

    [Fact]
    public void An_extend_block_adds_a_static_factory()
    {
        Assert.Equal(5, Run("""
            struct P { v: int, }
            extend P {
                static fn zero(): P { return P { v = 0 }; }
                fn get(): int { return this.v; }
            }
            fn main(): int { return P.zero().get() + 5; }
            """));
    }

    [Fact]
    public void An_extend_block_adds_a_static_member_to_a_primitive()
    {
        Assert.Equal(6, Run("""
            extend int { static fn zero(): int { return 0; } }
            fn main(): int { return int.zero() + 6; }
            """));
    }

    [Fact]
    public void A_static_extension_takes_arguments()
    {
        Assert.Equal(11, Run("""
            struct P { v: int, }
            extend P { static fn make(n: int): P { return P { v = n }; } }
            fn main(): int { return P.make(11).v; }
            """));
    }

    // ------------------------------------------------------------------ counter-checks

    [Fact]
    public void An_instance_extension_called_on_the_type_is_still_refused()
    {
        // Without this, a lookup that accepted every extension would pass the tests above.
        Assert.Contains("LYR-SEM0055", Errors("""
            struct P { v: int, }
            extend P { fn get(): int { return this.v; } }
            fn main(): int { return P.get(); }
            """));
    }

    [Fact]
    public void An_unknown_static_member_is_still_refused()
    {
        Assert.Contains("LYR-SEM0012", Errors("""
            struct P { v: int, }
            extend P { static fn zero(): P { return P { v = 0 }; } }
            fn main(): int { return P.nope(); }
            """));
    }

    [Fact]
    public void A_static_enum_method_is_not_reachable_on_a_value()
    {
        Assert.Contains("LYR-SEM0055", Errors("""
            enum E { A; static fn make(): E { return E.A; } }
            fn main(): int { let e = E.A; let x = e.make(); return 0; }
            """));
    }
}
