using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// The conformance list of a struct, class, enum or extend block: every entry an interface
/// (LYR-SEM0080). The counterpart of the parent-list rule LYR-SEM0078 — before it, a known
/// non-interface entry ('struct S :: [int]') passed without any diagnostic, because every
/// conformance walk skips what <see cref="Conformance.InterfaceOf"/> cannot answer for.
/// An UNKNOWN name stays the resolver's error alone (LYR-RES0002).
/// </summary>
public class ConformanceListTests
{
    private static DiagnosticEngine Check(string source)
    {
        var sm = new SourceManager();
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, sm.AddVirtual("main.lyr", source), de).ParseModule(), "main");
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    private static void AssertRefused(string source, string listOwner)
    {
        var de = Check(source);
        var error = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0080");
        Assert.Contains($"conformance list of '{listOwner}'", error.Message, StringComparison.Ordinal);
    }

    // --- a known non-interface entry is refused, one message per entry ---

    [Fact]
    public void A_builtin_in_a_struct_list_is_refused() =>
        AssertRefused("struct S :: [int] { x: int, }", "S");

    [Fact]
    public void A_struct_in_a_struct_list_is_refused() =>
        AssertRefused("""
            struct P { x: int, }
            struct S :: [P] { y: int, }
            """, "S");

    [Fact]
    public void A_struct_in_a_class_list_is_refused() =>
        AssertRefused("""
            struct P { x: int, }
            class C :: [P] { y: int, }
            """, "C");

    [Fact]
    public void A_struct_in_an_enum_list_is_refused() =>
        AssertRefused("""
            struct P { x: int, }
            enum E :: [P] { A, B }
            """, "E");

    [Fact]
    public void A_struct_in_an_extend_list_is_refused() =>
        AssertRefused("""
            struct T { x: int, }
            struct P { y: int, }
            extend T :: [P] { }
            """, "T");

    [Fact]
    public void A_generic_instance_of_a_class_is_refused() =>
        AssertRefused("""
            class Box<T> { v: T, }
            struct S :: [Box<int>] { x: int, }
            """, "S");

    [Fact]
    public void A_type_parameter_is_refused() =>
        AssertRefused("struct S<T> :: [T] { x: T, }", "S");

    // --- what stays whose error ---

    [Fact]
    public void An_unknown_name_stays_the_resolvers_error_alone()
    {
        var de = Check("struct S :: [Nope] { x: int, }");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-RES0002");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0080");
    }

    [Fact]
    public void An_interface_beside_a_non_interface_is_still_checked()
    {
        // The good entry keeps its conformance check: the missing 'hp' reports SEM0020
        // next to the SEM0080 for the bad one.
        var de = Check("""
            interface Damageable { fn hp(): int; }
            struct P { x: int, }
            struct S :: [Damageable, P] { y: int, }
            """);
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0080");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0020");
    }

    [Fact]
    public void An_interface_entry_is_clean()
    {
        var de = Check("""
            interface Named { fn name(): string; }
            struct S :: [Named] {
                fn name(): string { return "s"; }
            }
            """);
        Assert.False(de.HasErrors, string.Join("; ", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }
}
