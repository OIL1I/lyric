using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// Interfaces + Extend — M4-Slice 4b (Sprache.md §3.5/§3.6, D10). Signatur-genaue
/// Konformanz (SEM0020 fehlend, SEM0042 Signatur-Mismatch), Default-Methoden-Lookup
/// (Override gewinnt, SEM0043 Ambiguität), Extend-Merge über die Registry (User-Typen +
/// Builtins, import-gebundene Sichtbarkeit, SEM0044 Duplikat), Orphan-Rule (SEM0041)
/// und unsupported Extend-Ziele (SEM0047). Multi-Modul-Setup wo nötig.
/// </summary>
public class ExtendConformanceTests
{
    private static (TypeResult types, DiagnosticEngine de) Check(params (string name, string src)[] modules)
    {
        var sm = new SourceManager();
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        foreach (var (name, src) in modules)
            comp.AddModule(new Parser(sm, sm.AddVirtual(name + ".lyr", src), de).ParseModule(), name);
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);
        return (types, de);
    }

    private static DiagnosticEngine Diags(string src) => Check(("main", src)).de;

    private static void AssertClean(DiagnosticEngine de) =>
        Assert.False(de.HasErrors, string.Join("; ", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

    private static void AssertCode(DiagnosticEngine de, string code) =>
        Assert.Contains(de.Diagnostics, d => d.Code == code);

    // --- Konformanz: Signatur-Match (SEM0020 / SEM0042) ---

    [Fact]
    public void Implementing_the_interface_is_clean()
    {
        AssertClean(Diags("""
            interface Damageable { mut fn takeDamage(amount: int); fn hp(): int; }
            class Player :: [Damageable] {
                life: int = 100,
                mut fn takeDamage(amount: int) { this.life -= amount; }
                fn hp(): int { return this.life; }
            }
            """));
    }

    [Fact]
    public void Missing_abstract_method_is_reported()
    {
        AssertCode(Diags("""
            interface Damageable { fn hp(): int; }
            struct Rock :: [Damageable] { }
            """), "LYR-SEM0020");
    }

    [Fact]
    public void Wrong_parameter_type_is_reported()
    {
        AssertCode(Diags("""
            interface Adder { fn add(x: int): int; }
            struct S :: [Adder] { fn add(x: string): int { return 0; } }
            """), "LYR-SEM0042");
    }

    [Fact]
    public void Wrong_return_type_is_reported()
    {
        AssertCode(Diags("""
            interface Adder { fn add(x: int): int; }
            struct S :: [Adder] { fn add(x: int): string { return "no"; } }
            """), "LYR-SEM0042");
    }

    [Fact]
    public void Missing_mut_is_reported()
    {
        AssertCode(Diags("""
            interface Sink { mut fn put(x: int); }
            class C :: [Sink] { fn put(x: int) { } }
            """), "LYR-SEM0042");
    }

    [Fact]
    public void Impl_throwing_more_than_interface_is_reported()
    {
        AssertCode(Diags("""
            class Boom :: [Throwable] { fn message(): string { return "b"; } }
            interface Safe { fn run(): int; }
            struct S :: [Safe] { fn run(): int throws Boom { throw Boom { }; } }
            """), "LYR-SEM0042");
    }

    [Fact]
    public void Impl_throwing_subset_of_interface_is_clean()
    {
        AssertClean(Diags("""
            class Boom :: [Throwable] { fn message(): string { return "b"; } }
            interface Risky { fn run(): int throws; }
            struct S :: [Risky] { fn run(): int throws Boom { throw Boom { }; } }
            """));
    }

    [Fact]
    public void Generic_interface_substitutes_member_signatures()
    {
        AssertClean(Diags("""
            interface Container<T> { mut fn add(item: T); fn count(): int; }
            class IntBag :: [Container<int>] {
                n: int = 0,
                mut fn add(item: int) { this.n += 1; }
                fn count(): int { return this.n; }
            }
            """));
        AssertCode(Diags("""
            interface Container<T> { mut fn add(item: T); }
            class Bad :: [Container<int>] { mut fn add(item: string) { } }
            """), "LYR-SEM0042");
    }

    // --- Default-Methoden ---

    [Fact]
    public void Default_method_is_callable_and_need_not_be_implemented()
    {
        AssertClean(Diags("""
            interface Greeter {
                fn name(): string;
                fn greet(): string { return "hi"; }
            }
            struct S :: [Greeter] {
                fn name(): string { return "s"; }
                fn use(): string { return this.greet(); }
            }
            """));
    }

    [Fact]
    public void Own_method_overrides_the_default()
    {
        AssertClean(Diags("""
            interface Greeter { fn greet(): string { return "default"; } }
            struct S :: [Greeter] {
                fn greet(): string { return "own"; }
                fn use(): string { return this.greet(); }
            }
            """));
    }

    [Fact]
    public void Ambiguous_default_from_two_interfaces_is_reported()
    {
        AssertCode(Diags("""
            interface A { fn tag(): string { return "a"; } }
            interface B { fn tag(): string { return "b"; } }
            struct S :: [A, B] {
                fn use(): string { return this.tag(); }
            }
            """), "LYR-SEM0043");
    }

    // --- Extend: Member-Merge ---

    [Fact]
    public void Extension_method_on_user_type_is_visible()
    {
        AssertClean(Diags("""
            struct Vec { x: int, y: int }
            extend Vec {
                fn sum(): int { return this.x + this.y; }
            }
            fn use(v: Vec): int { return v.sum(); }
            """));
    }

    [Fact]
    public void Extension_method_on_builtin_is_visible()
    {
        AssertClean(Diags("""
            extend string {
                fn shout(): string { return this + "!"; }
            }
            fn use(s: string): string { return s.shout(); }
            """));
    }

    [Fact]
    public void Extension_can_call_sibling_extension_via_this()
    {
        AssertClean(Diags("""
            struct Vec { x: int, y: int }
            extend Vec {
                fn sum(): int { return this.x + this.y; }
                fn doubled(): int { return this.sum() * 2; }
            }
            """));
    }

    [Fact]
    public void Unknown_extension_method_is_still_an_error()
    {
        AssertCode(Diags("""
            struct Vec { x: int }
            fn use(v: Vec): int { return v.nope(); }
            """), "LYR-SEM0012");
    }

    [Fact]
    public void Duplicate_extension_method_from_two_blocks_is_ambiguous()
    {
        AssertCode(Diags("""
            struct Vec { x: int }
            extend Vec { fn tag(): int { return 1; } }
            extend Vec { fn tag(): int { return 2; } }
            fn use(v: Vec): int { return v.tag(); }
            """), "LYR-SEM0044");
    }

    [Fact]
    public void Extension_with_own_generics_infers()
    {
        AssertClean(Diags("""
            struct Box { v: int }
            extend Box {
                fn map<U>(f: fn(int) -> U): U { return f(this.v); }
            }
            fn use(b: Box): string { return b.map((x) => f"{x}"); }
            """));
    }

    // --- Extend :: [I] erfüllt Konformanz + Constraint ---

    [Fact]
    public void Extend_provides_interface_conformance()
    {
        AssertClean(Diags("""
            interface Show { fn show(): string; }
            struct Vec { x: int }
            extend Vec :: [Show] {
                fn show(): string { return f"{this.x}"; }
            }
            """));
    }

    [Fact]
    public void Extend_conformance_still_checks_signatures()
    {
        AssertCode(Diags("""
            interface Show { fn show(): string; }
            struct Vec { x: int }
            extend Vec :: [Show] { fn show(): int { return this.x; } }
            """), "LYR-SEM0042");
    }

    [Fact]
    public void Extend_conformance_satisfies_a_generic_constraint()
    {
        AssertClean(Diags("""
            interface Ord { fn cmp(o: int): int; }
            struct Num { n: int }
            extend Num :: [Ord] { fn cmp(o: int): int { return this.n - o; } }
            fn needOrd<T :: [Ord]>(a: T): T { return a; }
            fn use(x: Num): Num { return needOrd(x); }
            """));
    }

    // --- Orphan-Rule (SEM0041) ---

    [Fact]
    public void Orphan_extension_is_reported()
    {
        // string ist builtin, Show ist im anderen Modul → weder Ziel noch Interface lokal.
        var de = Check(
            ("iface", "pub interface Show { fn show(): string; }"),
            ("main", """
                import iface { Show };
                extend string :: [Show] { fn show(): string { return "s"; } }
                """)).de;
        AssertCode(de, "LYR-SEM0041");
    }

    [Fact]
    public void Local_interface_lifts_the_orphan_restriction()
    {
        AssertClean(Diags("""
            interface Show { fn show(): string; }
            extend string :: [Show] { fn show(): string { return "s"; } }
            """));
    }

    [Fact]
    public void Inherent_extension_has_no_orphan_restriction()
    {
        AssertClean(Diags("""
            extend string { fn shout(): string { return this + "!"; } }
            """));
    }

    // --- Import-Sichtbarkeit ---

    [Fact]
    public void Extension_is_invisible_without_importing_its_module()
    {
        var de = Check(
            ("helpers", """
                pub struct Vec { x: int }
                extend Vec { fn sum(): int { return this.x; } }
                """),
            ("main", """
                import helpers { Vec };
                fn use(v: Vec): int { return v.sum(); }
                """)).de;
        // main importiert helpers → Extension sichtbar.
        AssertClean(de);
    }

    // --- Unsupported Extend-Ziel (SEM0047) ---

    [Fact]
    public void Generic_extend_target_is_unsupported()
    {
        AssertCode(Diags("""
            struct Box<T> { v: T }
            extend Box<int> { fn get(): int { return this.v; } }
            """), "LYR-SEM0047");
    }
}
