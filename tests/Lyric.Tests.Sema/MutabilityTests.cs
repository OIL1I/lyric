using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// What `let` means for a reference type.
///
/// <para>It binds the NAME rather than the content. A `class` object and a `T[]` are both reference
/// types and therefore behave the same: the name cannot be rebound, the object behind it can.</para>
///
/// <para>THIS FILE AROSE BECAUSE IT DID NOT EXIST. The old rule — "the container has to be mut" — stood
/// in the specification and was secured by NOT A SINGLE TEST. That is exactly why its contradiction
/// survived two milestones: it forbade `xs[0] = 9` but let `ps[0].hp = 9` through, so it protected
/// nothing.</para>
/// </summary>
public class MutabilityTests
{
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

    private static void Allowed(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    private static void Rejected(string source) =>
        Assert.Contains(Check(source).Diagnostics, d => d.Code == "LYR-SEM0019");

    // ------------------------------------------------------------------ the three cases

    [Fact]
    public void A_class_field_is_writable_through_a_let_binding() =>
        Allowed("""
            class P { hp: int }
            fn main(): int { let p = P { hp = 1 }; p.hp = 9; return p.hp; }
            """);

    [Fact]
    public void An_array_element_is_writable_through_a_let_binding() =>
        // This used to be LYR-SEM0019, and as the only one of the three cases, although all three touch
        // the same reference type.
        Allowed("fn main(): int { let xs = [1, 2]; xs[0] = 9; return xs[0]; }");

    [Fact]
    public void A_field_of_an_array_element_was_always_writable() =>
        // The case that devalued the old rule: through the `let` array the content of an element could be
        // changed. What was forbidden was exactly the one operation one could avoid by taking an element
        // with a field.
        Allowed("""
            class P { hp: int }
            fn main(): int { let ps = [P { hp = 1 }]; ps[0].hp = 9; return ps[0].hp; }
            """);

    // ------------------------------------------------------------------ was weiterhin gilt

    [Fact]
    public void A_let_binding_cannot_be_rebound() =>
        // The core of the rule, and it stays: `let` pins the NAME. Without this test the rule would be
        // green even if `let` meant nothing at all any more.
        Rejected("fn main(): int { let x = 1; x = 2; return x; }");

    [Fact]
    public void An_array_bound_with_let_cannot_be_rebound() =>
        Rejected("fn main(): int { let xs = [1, 2]; xs = [3]; return xs[0]; }");

    [Fact]
    public void A_parameter_cannot_be_assigned() =>
        // Parameters are immutable, and that rule changes nothing about it.
        Rejected("fn f(n: int): int { n = 2; return n; }\nfn main(): int { return f(1); }");

    [Fact]
    public void A_struct_field_is_writable_through_let() =>
        // The reverse of before: `A_struct_field_still_needs_a_mutable_base` stood here, because the rule
        // spoke only about reference types. The exception was deleted after measuring that it held
        // nothing: `let v = V { x = 1 }; v.shift(9);` with a `mut fn` always passed AND really changed v.
        // What was forbidden was only the spelling that can be replaced.
        Allowed("""
            struct V { x: int, }
            fn main(): int { let v = V { x = 1 }; v.x = 9; return v.x; }
            """);

    [Fact]
    public void A_var_struct_field_is_writable() =>
        Allowed("""
            struct V { x: int, }
            fn main(): int { var v = V { x = 1 }; v.x = 9; return v.x; }
            """);

    [Fact]
    public void A_struct_parameter_field_is_writable() =>
        // The change hits the COPY; that the caller does not see it is checked by
        // `A_struct_parameter_keeps_value_semantics` in the VM tests. Here it is only about the sema
        // allowing it.
        Allowed("""
            struct V { x: int, }
            fn f(v: V): int { v.x = 9; return v.x; }
            fn main(): int { return f(V { x = 1 }); }
            """);

    [Fact]
    public void A_non_mut_method_still_cannot_touch_this() =>
        // The only thing the old chain ever protected, and it stays: the promise of `mut fn`. Without this
        // test the rule would be green even if `mut` became meaningless.
        Rejected("""
            struct V { x: int, fn peek(): int { this.x = 9; return this.x; } }
            fn main(): int { return 0; }
            """);
}
