using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Tests.Vm;

/// <summary>
/// Optional chaining with a CALL: <c>b?.get()</c>.
///
/// <para>This used to be <c>LYR-SEM0013: '?fn() -&gt; int' is not callable</c>, a statement about an
/// intermediate type nobody wrote down. Field access (<c>b?.v</c>) worked; the call did not, and the
/// way out, <c>if (b != null) { b.get() }</c>, is three times as long.</para>
///
/// <para>THE CALL RUNS THROUGH THE SAME RESOLUTION AS ANY OTHER, only with an already unwrapped
/// receiver. A path of its own would have had to answer virtual dispatch, natives, extensions and
/// generics a second time.</para>
/// </summary>
public class OptionalCallTests
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
        Assert.NotNull(ir);
        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;
    }

    private const string Box = """
        class Box {
            v: int = 7,
            fn get(): int { return this.v; }
            fn plus(n: int): int { return this.v + n; }
            fn leer(): ?int { return null; }
        }

        """;

    [Fact]
    public void A_call_on_a_present_receiver_returns_its_value() =>
        Assert.Equal(7, Run(Box + """
            fn main(): int {
                let b: ?Box = Box { };
                return b?.get() ?? -1;
            }
            """));

    [Fact]
    public void A_call_on_an_absent_receiver_yields_none() =>
        Assert.Equal(-1, Run(Box + """
            fn main(): int {
                let b: ?Box = null;
                return b?.get() ?? -1;
            }
            """));

    [Fact]
    public void Arguments_are_passed_through() =>
        Assert.Equal(10, Run(Box + """
            fn main(): int {
                let b: ?Box = Box { };
                return b?.plus(3) ?? -1;
            }
            """));

    /// <summary>
    /// An empty receiver does not call and therefore does not evaluate the arguments either. The test
    /// measures that with a side effect; without it, it would stay green if the arguments were computed
    /// before the check.
    /// </summary>
    [Fact]
    public void An_absent_receiver_does_not_evaluate_the_arguments() =>
        Assert.Equal(0, Run(Box + """
            class Zaehler { stand: int = 0 }
            let z = Zaehler { };

            fn mitzaehlen(): int { z.stand = z.stand + 1; return 1; }

            fn main(): int {
                let b: ?Box = null;
                let ignoriert = b?.plus(mitzaehlen()) ?? -1;
                return z.stand;
            }
            """));

    /// <summary>
    /// When the method itself yields an optional, it stays at ONE level: optionals do not nest. Without
    /// the collapse this would be a <c>??int</c>, which the lowering rejects as <c>LYR-IR0001</c>.
    /// </summary>
    [Fact]
    public void A_method_returning_an_optional_does_not_nest() =>
        Assert.Equal(-1, Run(Box + """
            fn main(): int {
                let b: ?Box = Box { };
                return b?.leer() ?? -1;
            }
            """));

    /// <summary>And the same with an empty receiver: both routes have to arrive at the same
    /// level.</summary>
    [Fact]
    public void A_method_returning_an_optional_on_an_absent_receiver() =>
        Assert.Equal(-1, Run(Box + """
            fn main(): int {
                let b: ?Box = null;
                return b?.leer() ?? -1;
            }
            """));

    /// <summary>
    /// The counter-check: an ordinary call without <c>?.</c> stays ordinary. That is the form standing in
    /// every program, and it runs through the same function.
    /// </summary>
    [Fact]
    public void An_ordinary_method_call_still_works() =>
        Assert.Equal(7, Run(Box + """
            fn main(): int {
                let b = Box { };
                return b.get();
            }
            """));

    /// <summary>And the field access through <c>?.</c>.</summary>
    [Fact]
    public void Optional_field_access_still_works() =>
        Assert.Equal(7, Run(Box + """
            fn main(): int {
                let b: ?Box = Box { };
                return b?.v ?? -1;
            }
            """));

    /// <summary>
    /// A field that is itself optional. The sema used to make a <c>??int</c> of it, and the error arrived
    /// one level too late as "cannot assign '?int' to 'int'".
    /// </summary>
    [Fact]
    public void Optional_field_access_onto_an_optional_field_collapses() =>
        Assert.Equal(5, Run("""
            class B { w: ?int = 5, }
            fn main(): int {
                let b: ?B = B { };
                return b?.w ?? -1;
            }
            """));

    // ------------------------------------------------------- the other kinds of receiver
    //
    // They stand here because a first fix would have broken them ALL: it hung the unwrapped receiver on a
    // special case in the callee 'switch', and that stood before the generics and the interface detection.
    // 'Box<int>' was then reported as "external or bodiless" — a diagnostic on the wrong cause, and one
    // no test would have noticed.

    /// <summary>Dynamic dispatch over an unwrapped receiver.</summary>
    [Fact]
    public void A_call_on_an_interface_value_dispatches_virtually() =>
        Assert.Equal(3, Run("""
            interface Zeigbar { fn zeig(): int; }
            class A :: [Zeigbar] { fn zeig(): int { return 3; } }

            fn main(): int {
                let z: ?Zeigbar = A { };
                return z?.zeig() ?? -1;
            }
            """));

    /// <summary>A generic instance: the method belongs to <c>Box&lt;int&gt;</c> rather than to
    /// <c>Box</c>, and only there is its return type an <c>int</c>.</summary>
    [Fact]
    public void A_call_on_a_generic_instance_uses_the_instance_method() =>
        Assert.Equal(4, Run("""
            class Box<T> { v: T, fn get(): T { return this.v; } }

            fn main(): int {
                let b: ?Box<int> = Box<int> { v = 4 };
                return b?.get() ?? -1;
            }
            """));

    /// <summary>A type parameter with a constraint: the third dispatch route.</summary>
    [Fact]
    public void A_call_on_a_constrained_type_parameter_works() =>
        Assert.Equal(3, Run("""
            interface Zeigbar { fn zeig(): int; }
            class A :: [Zeigbar] { fn zeig(): int { return 3; } }

            fn nimm<T :: [Zeigbar]>(x: ?T): int { return x?.zeig() ?? -1; }
            fn main(): int { let a: ?A = A { }; return nimm(a); }
            """));

    /// <summary>A method from an <c>extend</c> block: the fourth route.</summary>
    [Fact]
    public void A_call_on_an_extension_method_works() =>
        Assert.Equal(9, Run("""
            class Leer { }
            extend Leer { fn neun(): int { return 9; } }

            fn main(): int {
                let l: ?Leer = Leer { };
                return l?.neun() ?? -1;
            }
            """));

    /// <summary>
    /// Two chains inside one another. They carry different AST nodes and must therefore not overwrite each
    /// other's unwrapped receiver — the promise a global "current chain" would have failed.
    /// </summary>
    [Fact]
    public void Nested_chains_do_not_interfere() =>
        Assert.Equal(14, Run(Box + """
            fn main(): int {
                let a: ?Box = Box { };
                let b: ?Box = Box { };
                return a?.plus(b?.get() ?? 0) ?? -1;
            }
            """));

    /// <summary>And a chain on a chain: <c>a?.b?.c()</c>.</summary>
    [Fact]
    public void A_chain_on_a_chain_works() =>
        Assert.Equal(7, Run("""
            class Box { v: int = 7, fn get(): int { return this.v; } }
            class Aussen { inner: ?Box, }

            fn main(): int {
                let a: ?Aussen = Aussen { inner = Box { } };
                return a?.inner?.get() ?? -1;
            }
            """));

    /// <summary>
    /// A PRIMITIVE receiver with an inherent extension. It is the fifth dispatch route and stood in the
    /// same case distinction: it would have fallen through on the first attempt, and not into a diagnostic
    /// but into a call without a receiver.
    /// </summary>
    [Fact]
    public void A_call_on_a_primitive_receiver_works() =>
        Assert.Equal(12, Run("""
            extend int { fn doppelt(): int { return this * 2; } }

            fn main(): int {
                let n: ?int = 6;
                return n?.doppelt() ?? -1;
            }
            """));
}
