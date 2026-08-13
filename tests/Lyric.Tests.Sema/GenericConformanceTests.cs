using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Sema;

/// <summary>
/// Conformance against a GENERIC interface: <c>Src&lt;int&gt;</c> and <c>Src&lt;string&gt;</c> are the
/// same symbol and different requirements.
///
/// <para>The conformance used to compare only the interface SYMBOL. A
/// <c>class Ones :: [Src&lt;int&gt;]</c> therefore satisfied a <c>&lt;T :: [Src&lt;string&gt;]&gt;</c>, and
/// the body put an <c>i64</c> into a <c>string</c> slot. Measured:</para>
/// <list type="bullet">
///   <item><description>DEBUG: a verifier crash.</description></item>
///   <item><description>RELEASE — what gets shipped — RAN THROUGH and gave a silently wrong answer. The
///     bytecode loader did not catch it either.</description></item>
/// </list>
///
/// <para>That is not a missing feature but a type checker accepting a program whose types do not hold.
/// That .NET contains the damage — an empty string rather than a memory fault — is luck of the value
/// representation and no promise of the language.</para>
///
/// <para>BOTH DIRECTIONS STAND HERE: the wrong instantiation has to fail, and the right one has to keep
/// passing. A fix checking only the first half could make <c>Map&lt;K, V&gt;</c> and
/// <c>Iterator&lt;T&gt;</c> unusable without it
/// auffiele.</para>
/// </summary>
public class GenericConformanceTests
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

    private static void Compiles(string source)
    {
        var de = Check(source);
        Assert.False(de.HasErrors,
            string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    private static void Rejects(string source)
    {
        var de = Check(source);
        Assert.Contains(de.Diagnostics, d => d.Code is "LYR-SEM0028" or "LYR-SEM0001");
    }

    private const string Interface = """
        interface Src<T> { fn hole(): T; }

        class Ones :: [Src<int>] { fn hole(): int { return 1; } }
        class Worte :: [Src<string>] { fn hole(): string { return "a"; } }

        """;

    // ------------------------------------------------------------------ Constraints

    /// <summary>The reported case.</summary>
    [Fact]
    public void A_constraint_rejects_the_wrong_type_argument() =>
        Rejects(Interface + """
            fn nimm<T :: [Src<string>]>(s: T): string { return s.hole(); }
            fn main(): int { let x = nimm(Ones { }); return 0; }
            """);

    /// <summary>The counter-check: without it a fix rejecting EVERYTHING would be green.</summary>
    [Fact]
    public void A_constraint_accepts_the_right_type_argument() =>
        Compiles(Interface + """
            fn nimm<T :: [Src<string>]>(s: T): string { return s.hole(); }
            fn main(): int { let x = nimm(Worte { }); return 0; }
            """);

    /// <summary>
    /// A constraint naming its own type parameter. It is the reason the full substitution map is passed
    /// through rather than one parameter at a time.
    /// </summary>
    [Fact]
    public void A_constraint_over_its_own_parameter_still_works() =>
        Compiles("""
            interface Eq<T> { fn gleich(other: T): bool; }
            class P :: [Eq<P>] { fn gleich(other: P): bool { return true; } }

            fn beide<T :: [Eq<T>]>(a: T, b: T): bool { return a.gleich(b); }
            fn main(): int { let x = beide(P { }, P { }); return 0; }
            """);

    /// <summary>A generic class satisfies the interface with ITS OWN type argument:
    /// a <c>Box&lt;int&gt;</c> is a <c>Src&lt;int&gt;</c> and no <c>Src&lt;string&gt;</c>.
    /// </summary>
    [Fact]
    public void A_generic_class_conforms_with_its_own_type_argument() =>
        Compiles("""
            interface Src<T> { fn hole(): T; }
            class Box<T> :: [Src<T>] { v: T, fn hole(): T { return this.v; } }

            fn nimm<T :: [Src<int>]>(s: T): int { return s.hole(); }
            fn main(): int { return nimm(Box<int> { v = 1 }); }
            """);

    [Fact]
    public void A_generic_class_with_the_wrong_argument_is_rejected() =>
        Rejects("""
            interface Src<T> { fn hole(): T; }
            class Box<T> :: [Src<T>] { v: T, fn hole(): T { return this.v; } }

            fn nimm<T :: [Src<int>]>(s: T): int { return s.hole(); }
            fn main(): int { return nimm(Box<string> { v = "a" }); }
            """);

    // ------------------------------------------------------------------ assignment

    /// <summary>
    /// The same question at the second place: an assignment to an interface type. It went through the
    /// same comparison and therefore had the same gap — ONE QUESTION, TWO PLACES.
    /// </summary>
    [Fact]
    public void An_assignment_to_a_generic_interface_checks_the_type_argument() =>
        Rejects(Interface + """
            fn main(): int {
                let s: Src<string> = Ones { };
                return 0;
            }
            """);

    [Fact]
    public void An_assignment_with_the_right_argument_still_works() =>
        Compiles(Interface + """
            fn main(): int {
                let s: Src<string> = Worte { };
                return 0;
            }
            """);

    // ------------------------------------------------------------------ non-generic

    /// <summary>
    /// An interface WITHOUT type arguments still compares through the symbol: there is nothing to
    /// distinguish there. Without this test it would go unnoticed if the stricter comparison took the
    /// ordinary case along with it, and that is the majority of all code.
    /// </summary>
    [Fact]
    public void A_plain_interface_still_conforms() =>
        Compiles("""
            interface Zeigbar { fn zeig(): string; }
            class A :: [Zeigbar] { fn zeig(): string { return "a"; } }

            fn nimm<T :: [Zeigbar]>(x: T): string { return x.zeig(); }
            fn main(): int { let s = nimm(A { }); return 0; }
            """);

    /// <summary>And through an <c>extend</c> block: the second route to conformance, running through the
    /// same function.</summary>
    [Fact]
    public void Conformance_through_an_extend_block_still_works() =>
        Compiles("""
            interface Src<T> { fn hole(): T; }
            class Leer { }
            extend Leer :: [Src<int>] { fn hole(): int { return 0; } }

            fn nimm<T :: [Src<int>]>(s: T): int { return s.hole(); }
            fn main(): int { return nimm(Leer { }); }
            """);

    [Fact]
    public void An_extend_block_with_the_wrong_argument_is_rejected() =>
        Rejects("""
            interface Src<T> { fn hole(): T; }
            class Leer { }
            extend Leer :: [Src<int>] { fn hole(): int { return 0; } }

            fn nimm<T :: [Src<string>]>(s: T): string { return s.hole(); }
            fn main(): int { let x = nimm(Leer { }); return 0; }
            """);
}
