using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;
using System.Runtime.CompilerServices;

namespace Lyric.Tests.Vm;

/// <summary>
/// `extend` blocks, inherent and through an interface.
///
/// <para>An extension method is an ordinary function with the receiver as parameter 0. No new IR type,
/// no opcode, no format bump: the inherent call is a direct <c>call</c>, because the compiler knows
/// statically which type stands at the receiver. The interface form fills the same vtable row as a
/// declared conformance, and which of the two established it is no longer distinguishable at runtime.
/// </para>
///
/// <para>Three tests here matter more than the rest, and each measures something that would otherwise
/// be silently wrong. First: an extension does NOT displace a member of the same name — the sema does
/// not report the case, it simply lets the own member win — and without the <c>&lt;extend&gt;</c> infix
/// in the mangling a cleanly type-checked program crashes in the verifier. Second: a builtin receiver
/// arrives as argument 0, which failed while building, because a scalar is no <c>NamedRef</c> and
/// therefore fell into the type or module branch of the call lowering. Third: the conformance tests
/// carry TWO implementations, one declared and one through <c>extend</c>; with only one the test would
/// stay green even if the dispatch bound statically to the first function it found.</para>
/// </summary>
public class ExtendTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>Lowers and returns the IR, for the tests interested not in the result but in WHAT stands
    /// in the module.</summary>
    private static IrModule Lower(string source)
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

        // optimize:false — the tests on this helper pin WHICH functions the lowering produces,
        // and the inliner would fold the very extension they look for.
        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true, optimize: false);
        Assert.NotNull(ir);
        return ir!;
    }

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
        Assert.False(de.HasErrors, "source did not compile:\n" + writer);

        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true);
        Assert.NotNull(ir);
        return Interpreter.Run(BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir!)),
            NativeRegistry.CreateDefault(TextWriter.Null, TextWriter.Null)).AsI64;
    }

    // ------------------------------------------------------------------ receiver types

    [Fact]
    public void A_class_can_be_extended() =>
        Assert.Equal(42, Run("""
            class Player { hp: int }
            extend Player { fn doubled(): int { return this.hp * 2; } }
            fn main(): int { let p = Player { hp = 21 }; return p.doubled(); }
            """));

    [Fact]
    public void A_struct_can_be_extended() =>
        // The receiver of a struct extension is the value itself, the same convention as for a struct
        // method and for the same reason no special case in the lowering.
        Assert.Equal(7, Run("""
            struct Vec { x: int, y: int, }
            extend Vec { fn sum(): int { return this.x + this.y; } }
            fn main(): int { let v = Vec { x = 3, y = 4 }; return v.sum(); }
            """));

    [Fact]
    public void An_enum_can_be_extended() =>
        Assert.Equal(2, Run("""
            enum Color { Red, Green }
            extend Color {
                fn rank(): int {
                    match (this) { Color.Red => { return 1; }, Color.Green => { return 2; } }
                }
            }
            fn main(): int { let c = Color.Green; return c.rank(); }
            """));

    [Fact]
    public void A_builtin_scalar_can_be_extended() =>
        // An 'int' as parameter 0 needs no boxing and no fat pointer, which is exactly why the inherent
        // form is cheap and the interface form is not.
        Assert.Equal(42, Run("""
            extend int { fn double(): int { return this * 2; } }
            fn main(): int { let n = 21; return n.double(); }
            """));

    [Fact]
    public void A_builtin_reference_type_can_be_extended() =>
        // 'string' is the other builtin case: a reference rather than a scalar. The body deliberately
        // avoids concatenation, which lowers to 'std.string.concat', and this harness binds no natives.
        // What is measured is the receiver, not the stdlib.
        Assert.Equal(4, Run("""
            extend string { fn tag(): int { return 4; } }
            fn main(): int { let s = "ab"; return s.tag(); }
            """));

    // ------------------------------------------------------------------ Namensraum

    [Fact]
    public void An_extension_does_not_displace_a_member_of_the_same_name() =>
        // An own member beats an extension. The sema does NOT report the shadowing: it lets the class
        // method win and makes the extension dead code. Without the <extend> infix in the mangling both
        // would be called 'test.Player.get', and the verifier rejects duplicate function names, so a
        // cleanly type-checked program would crash in the lowering.
        Assert.Equal(1, Run("""
            class Player { hp: int, fn get(): int { return this.hp; } }
            extend Player { fn get(): int { return 99; } }
            fn main(): int { let p = Player { hp = 1 }; return p.get(); }
            """));

    [Fact]
    public void Two_extensions_on_the_same_type_coexist() =>
        Assert.Equal(30, Run("""
            class Item { n: int }
            extend Item { fn a(): int { return this.n * 2; } }
            extend Item { fn b(): int { return this.n * 4; } }
            fn main(): int { let i = Item { n = 5 }; return i.a() + i.b(); }
            """));

    [Fact]
    public void An_extension_method_can_call_another_one() =>
        // Both get their FunctionId in pass 1, before any body is lowered; otherwise the forward call
        // here would fail, exactly as for ordinary functions.
        Assert.Equal(20, Run("""
            class Item { n: int }
            extend Item {
                fn once(): int { return this.n * 2; }
                fn twice(): int { return this.once() * 2; }
            }
            fn main(): int { let i = Item { n = 5 }; return i.twice(); }
            """));

    [Fact]
    public void An_extension_takes_parameters_after_the_receiver() =>
        // The receiver is parameter 0 and the written parameters follow. A test without parameters would
        // stay green even with the order swapped.
        Assert.Equal(23, Run("""
            class Item { n: int }
            extend Item { fn plus(k: int): int { return this.n + k; } }
            fn main(): int { let i = Item { n = 20 }; return i.plus(3); }
            """));

    // ------------------------------------------------------------------ extend T :: [I]  (P9b)

    [Fact]
    public void An_extension_can_supply_interface_conformance() =>
        // TWO implementations: with only one the test would stay green even if the dispatch bound
        // statically to the first function it found. One comes from an extend block, the other is
        // declared, and both have to fill the same vtable row, because at runtime it is no longer
        // distinguishable which route established the conformance.
        Assert.Equal(30, Run("""
            interface Scored { fn score(): int; }
            class A { n: int }
            class B :: [Scored] { n: int, fn score(): int { return 20; } }
            extend A :: [Scored] { fn score(): int { return 10; } }
            fn total(x: Scored, y: Scored): int { return x.score() + y.score(); }
            fn main(): int {
                let a = A { n = 1 };
                let b = B { n = 2 };
                return total(a, b);
            }
            """));

    [Fact]
    public void Extension_conformance_satisfies_an_assignment() =>
        // There used to be two answers to "does T satisfy the interface I": constraints knew about
        // extensions, assignments did not. This test and the next measure the same conformance over the
        // two paths that had drifted apart.
        Assert.Equal(7, Run("""
            interface Scored { fn score(): int; }
            class A { n: int }
            extend A :: [Scored] { fn score(): int { return 7; } }
            fn main(): int { let s: Scored = A { n = 1 }; return s.score(); }
            """));

    [Fact]
    public void Extension_conformance_satisfies_a_constraint() =>
        Assert.Equal(7, Run("""
            interface Scored { fn score(): int; }
            class A { n: int }
            extend A :: [Scored] { fn score(): int { return 7; } }
            fn get<T :: [Scored]>(x: T): int { return x.score(); }
            fn main(): int { return get(A { n = 1 }); }
            """));

    [Fact]
    public void An_own_member_still_beats_the_extension_in_the_vtable() =>
        // The rule applies to the vtable row too, not only to the direct call: the class method fills the
        // slot and the extension of the same name stays dead code.
        Assert.Equal(3, Run("""
            interface Scored { fn score(): int; }
            class A :: [Scored] { n: int, fn score(): int { return 3; } }
            extend A { fn score(): int { return 99; } }
            fn main(): int { let s: Scored = A { n = 1 }; return s.score(); }
            """));

    // ------------------------------------------------------------------ am M7-Gate gefunden

    // The three cases that follow have nothing to do with 'extend': they came to light when
    // 'examples/inventory.lyr' first ran through. That is the purpose of a gate — a program loading
    // several slices at once finds the edges between them.

    [Fact]
    public void An_interface_default_method_works_on_a_concrete_receiver() =>
        // The default method belongs to the INTERFACE and its 'this' is the interface type, so a direct
        // call does not lead there. The receiver is lifted (mkiface), then called virtually. Without
        // that: "call to main.Priced.isFree: arg 0 is val ty0, expected dyn ty1".
        Assert.Equal(7, Run("""
            interface Priced {
                fn price(): int;
                fn isFree(): bool { return this.price() == 0; }
            }
            struct Item :: [Priced] { n: int, fn price(): int { return this.n; } }
            fn main(): int { let it = Item { n = 0 }; if (it.isFree()) { return 7; } return 1; }
            """));

    [Fact]
    public void An_own_member_still_beats_the_default_on_a_concrete_receiver() =>
        // The counter-check. Without it the test above would stay green even if EVERY call were lifted,
        // and then an overridden method would never run.
        Assert.Equal(3, Run("""
            interface Priced {
                fn price(): int;
                fn isFree(): bool { return true; }
            }
            struct Item :: [Priced] {
                n: int,
                fn price(): int { return this.n; }
                fn isFree(): bool { return false; }
            }
            fn main(): int { let it = Item { n = 1 }; if (it.isFree()) { return 9; } return 3; }
            """));

    [Fact]
    public void A_match_over_an_optional_tests_and_unwraps() =>
        // Two faults in one expression: 'null' as a pattern was lowered as an EQUALITY COMPARISON — there
        // is no null operand, it is 'optissome' — and the binding in the other arm stored the '?T' into a
        // 'T' slot. The sema gives the name the narrowed type; it still has to be unwrapped, because the
        // narrowing is a statement about control flow rather than about memory.
        Assert.Equal(5, Run("""
            struct Item { n: int, }
            fn find(): ?Item { return Item { n = 5 }; }
            fn main(): int {
                let a = find();
                return match (a) { null => 0, it => it.n };
            }
            """));

    [Fact]
    public void A_match_over_an_optional_takes_the_null_arm_when_empty() =>
        // The counter-check: without it the test above would stay green if 'null' matched everything.
        Assert.Equal(4, Run("""
            struct Item { n: int, }
            fn find(): ?Item { return null; }
            fn main(): int {
                let a = find();
                return match (a) { null => 4, it => it.n };
            }
            """));

    [Fact]
    public void For_in_over_an_array_works_inside_a_generic_function() =>
        // In a monomorphized instance the iterator has to be interned with the CONCRETE element type.
        // Without the substitution the type table looks for a class named 'T' and throws — the same
        // place the return type has to meet it.
        Assert.Equal(6, Run("""
            interface Priced { fn price(): int; }
            struct Item :: [Priced] { n: int, fn price(): int { return this.n; } }
            fn total<T :: [Priced]>(xs: T[]): int {
                var sum = 0;
                for (x in xs) { sum += x.price(); }
                return sum;
            }
            fn main(): int { return total([Item { n = 2 }, Item { n = 4 }]); }
            """));

    // ------------------------------------------------------------------ only what is used

    [Fact]
    public void An_extension_that_is_never_called_stays_out_of_the_module() =>
        // The invariant: a declared but never instantiated class does not belong in the bytecode, and the
        // same now holds for extension methods.
        Assert.DoesNotContain(Lower("""
            class Item { n: int }
            extend Item { fn unused(): int { return 1; } }
            fn main(): int { let i = Item { n = 5 }; return i.n; }
            """).Functions, f => f.Name.Contains("unused"));

    [Fact]
    public void An_extension_that_is_called_is_in_the_module() =>
        // The counter-check. Without it the test above would stay green even if no extension were lowered
        // at all, and that would be the dangerous fault.
        Assert.Contains(Lower("""
            class Item { n: int }
            extend Item { fn used(): int { return 1; } }
            fn main(): int { let i = Item { n = 5 }; return i.used(); }
            """).Functions, f => f.Name.Contains("used"));

    [Fact]
    public void A_program_that_never_formats_carries_no_Display_machinery()
    {
        // The case that motivated this. 'std.core' is ALWAYS loaded, holding 'panic' and
        // 'coroutineEnded', so its five Display extensions used to lie in every program, together with
        // the four 'std.string' imports they need. A 'hello.lyr' paid for a function it never calls.
        var ir = Lower("fn main(): int { return 7; }");

        Assert.DoesNotContain(ir.Functions, f => f.Name.Contains("Display")
                                                 || f.Name.EndsWith(".show"));

        // What is checked is that the CONVERTERS are missing, not that the import table is empty.
        //
        // 'std.string' has its own Lyric bodies (parseInt, replace, isDigit, …), and they drag their
        // natives along as soon as the module is loaded, even when nobody calls them. That is the missing
        // reachability analysis rather than a regression in the Display machinery this is about.
        var importe = ir.Imports.Select(i => i.Name).ToArray();
        Assert.DoesNotContain("std.string.fromInt", importe);
        Assert.DoesNotContain("std.string.fromBool", importe);
        Assert.DoesNotContain("std.string.fromChar", importe);
    }

    [Fact]
    public void The_Display_machinery_appears_when_a_constraint_uses_it()
    {
        // The counter-check to the previous test and at the same time the proof that a builtin satisfies
        // 'Display' through an 'extend' in std.core, with the monomorphization turning it into a direct
        // call: no interface value, no boxing.
        var ir = Lower("""
            import std.core { Display };
            fn describe<T :: [Display]>(v: T): string { return v.show(); }
            fn main(): int { let s = describe(42); return 0; }
            """);

        Assert.Contains(ir.Functions, f => f.Name.Contains("<extend>.int.show"));
        Assert.Contains(ir.Imports, i => i.Name == "std.string.fromInt");

        // Only the type used: 'float' and 'bool' satisfy 'Display' just as well but are not called here.
        Assert.DoesNotContain(ir.Functions, f => f.Name.Contains("<extend>.float.show"));
    }

    // No test for 'static fn' in an extend block: the grammar allows a FunctionDecl there, and 'static'
    // is a MEMBER marker that does not belong to it, so the parser rejects it with LYR-PAR0008. Whether
    // that is intended is a language question rather than a lowering question and is not answered here.
}
