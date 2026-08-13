using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// A member without a marker is an instance member with a bound <c>this</c>; <c>static</c> means no
/// receiver.
///
/// <para>Before this decision EVERY METHOD WAS BOTH STATIC AND INSTANCE-BOUND — measured,
/// <c>P.getHp()</c> without a receiver, <c>p.new()</c> on an instance and even <c>this.hp</c> in a
/// method called as <c>P.new()</c> passed the type check. The last case would have produced a field
/// access without an object in the lowering.</para>
/// </summary>
public class StaticMemberTests
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

    private static void AssertReports(string source, string code)
    {
        var de = Check(source);
        Assert.Contains(de.Diagnostics, d => d.Code == code);
    }

    private static void AssertClean(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            "expected this to check clean, but got:\n" +
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    // --- Die Kreuzformen ---

    [Fact]
    public void An_instance_method_called_on_the_type_is_reported() =>
        AssertReports(
            """
            class P {
                hp: int,
                fn getHp(): int { return this.hp; }
            }
            fn main(): int { return P.getHp(); }
            """, "LYR-SEM0055");

    [Fact]
    public void A_static_method_called_on_an_instance_is_reported() =>
        AssertReports(
            """
            class P {
                hp: int,
                static fn make(): P { return P { hp = 1 }; }
            }
            fn main(): int { let p = P { hp = 2 }; return p.make().hp; }
            """, "LYR-SEM0055");

    /// <summary>The most dangerous case: <c>this</c> without any object. It passed, because a factory was
    /// the same method as an instance method.</summary>
    [Fact]
    public void This_inside_a_static_member_is_reported() =>
        AssertReports(
            """
            class P {
                hp: int,
                static fn make(): P { return P { hp = this.hp }; }
            }
            fn main(): int { return 0; }
            """, "LYR-SEM0008");

    [Fact]
    public void A_field_read_on_the_type_is_reported() =>
        AssertReports(
            """
            class P { hp: int }
            fn main(): int { return P.hp; }
            """, "LYR-SEM0055");

    // --- marker combinations ---

    [Fact]
    public void Static_and_mut_together_are_reported() =>
        AssertReports(
            """
            class P {
                hp: int,
                static mut fn f() { }
            }
            fn main(): int { return 0; }
            """, "LYR-SEM0054");

    /// <summary>
    /// <c>mut</c> on a class method stays ALLOWED. It enforces nothing there, but the documentation lists
    /// it explicitly as a readability convention, and interfaces declare <c>mut fn</c> that implementing
    /// classes have to satisfy.
    ///
    /// <para>The first version of the decision wanted to forbid it; the test holds why not.</para>
    /// </summary>
    [Fact]
    public void Mut_on_a_class_method_stays_legal() =>
        AssertClean(
            """
            interface Damageable { mut fn hurt(n: int); }
            class P :: [Damageable] {
                hp: int,
                mut fn hurt(n: int) { this.hp -= n; }
            }
            fn main(): int { return 0; }
            """);

    // --- Positiv ---

    [Fact]
    public void Static_members_and_instance_members_coexist() =>
        AssertClean(
            """
            class P {
                hp: int,

                static let ZERO: int = 0;

                static fn new(v: int): P { return P { hp = v }; }
                fn get(): int { return this.hp; }
            }
            fn main(): int { let p = P.new(7); return p.get() + P.ZERO; }
            """);

    /// <summary>A <c>static let</c> constant has no <c>this</c>: its initializer is checked without a
    /// receiver.</summary>
    [Fact]
    public void This_inside_a_static_binding_is_reported() =>
        AssertReports(
            """
            class P {
                hp: int,
                static let COPY: int = this.hp;
            }
            fn main(): int { return 0; }
            """, "LYR-SEM0008");
}
