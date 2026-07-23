using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// Exception-Sema — M4-Slice 3a (Sprache.md §9). Throwable-Constraint an throw/throws/
/// catch (SEM0030), try/catch-Struktur (SEM0035/0036), throws-Propagation über Calls
/// inkl. Interface-Widening und Catch-Zuordnung (SEM0034), throws-Funktionen als Wert
/// (SEM0037), Catch-Bindungs-Typen (typlos → Throwable) und panic → never.
/// Läuft über die volle Sema-Pipeline (Semantics.Analyze).
/// </summary>
public class ExceptionTests
{
    private const string Prelude = """
        interface IOError { fn message(): string; }
        class NotFound :: [Throwable] {
            path: string,
            fn message(): string { return this.path; }
        }
        class DbError :: [Throwable, IOError] {
            fn message(): string { return "db"; }
        }
        struct Plain { x: int }
        fn mayThrow(): int throws NotFound { return 1; }
        fn mayThrowDb(): int throws DbError { return 1; }
        fn mayThrowAny(): int throws { return 1; }
        fn safe(): int { return 1; }
        """;

    private static (TypeResult types, DiagnosticEngine de, Module module) Check(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        var module = new Parser(sm, id, de).ParseModule();
        comp.AddModule(module);
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);
        return (types, de, module);
    }

    private static DiagnosticEngine Diags(string body) => Check(Prelude + "\n" + body).de;

    private static void AssertClean(DiagnosticEngine de) =>
        Assert.False(de.HasErrors, string.Join("; ", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

    private static void AssertCode(DiagnosticEngine de, string code) =>
        Assert.Contains(de.Diagnostics, d => d.Code == code);

    // --- Throwable-Constraint (SEM0030) ---

    [Fact]
    public void Throw_of_throwable_class_is_clean()
    {
        AssertClean(Diags("""fn t(): int throws NotFound { throw NotFound { path = "x" }; }"""));
    }

    [Fact]
    public void Throw_of_non_throwable_is_reported()
    {
        AssertCode(Diags("fn t() throws { throw Plain { x = 1 }; }"), "LYR-SEM0030");
    }

    [Fact]
    public void Throws_clause_with_non_throwable_type_is_reported()
    {
        AssertCode(Diags("fn t(): int throws Plain { return 1; }"), "LYR-SEM0030");
    }

    [Fact]
    public void Catch_of_non_throwable_type_is_reported()
    {
        AssertCode(Diags("fn t() { try { safe(); } catch (e: Plain) { } }"), "LYR-SEM0030");
    }

    // --- try/catch-Struktur (SEM0035/0036) ---

    [Fact]
    public void Catch_all_must_be_last()
    {
        AssertCode(Diags("fn t() { try { safe(); } catch (e) { } catch (x: NotFound) { } }"), "LYR-SEM0035");
    }

    [Fact]
    public void Try_without_catch_is_reported()
    {
        AssertCode(Diags("fn t() { try { safe(); } }"), "LYR-SEM0036");
    }

    // --- Catch-Bindungen ---

    [Fact]
    public void Untyped_catch_binds_throwable_with_message()
    {
        // e: Throwable → e.message() ist string; kein SEM0018 auf e (Catch weist zu).
        AssertClean(Diags("fn t() { try { safe(); } catch (e) { let m: string = e.message(); } }"));
    }

    [Fact]
    public void Typed_catch_binds_the_declared_type()
    {
        AssertClean(Diags("""
            fn t() {
                try { mayThrow(); } catch (e: NotFound) { let p: string = e.path; }
            }
            """));
    }

    // --- Propagation: Handling über try ---

    [Fact]
    public void Unhandled_call_is_reported()
    {
        AssertCode(Diags("fn t() { let x = mayThrow(); }"), "LYR-SEM0034");
    }

    [Fact]
    public void Call_handled_by_matching_catch_is_clean()
    {
        AssertClean(Diags("fn t() { try { let x = mayThrow(); } catch (e: NotFound) { } }"));
    }

    [Fact]
    public void Call_handled_by_interface_catch_is_clean()
    {
        AssertClean(Diags("fn t() { try { mayThrowDb(); } catch (e: IOError) { } }"));
    }

    [Fact]
    public void Call_handled_by_catch_all_is_clean()
    {
        AssertClean(Diags("fn t() { try { mayThrow(); } catch (_) { } }"));
    }

    [Fact]
    public void Non_matching_catch_does_not_handle()
    {
        AssertCode(Diags("fn t() { try { mayThrow(); } catch (e: DbError) { } }"), "LYR-SEM0034");
    }

    [Fact]
    public void Outer_try_handles_through_inner()
    {
        AssertClean(Diags("""
            fn t() {
                try {
                    try { mayThrow(); } catch (e: DbError) { }
                } catch (e: NotFound) { }
            }
            """));
    }

    // --- Propagation: Handling über eigene throws-Klausel ---

    [Fact]
    public void Call_covered_by_exact_throws_is_clean()
    {
        AssertClean(Diags("fn t(): int throws NotFound { return mayThrow(); }"));
    }

    [Fact]
    public void Call_covered_by_untyped_throws_is_clean()
    {
        AssertClean(Diags("fn t(): int throws { return mayThrow(); }"));
    }

    [Fact]
    public void Call_covered_by_interface_throws_is_clean()
    {
        AssertClean(Diags("fn t(): int throws IOError { return mayThrowDb(); }"));
    }

    [Fact]
    public void Interface_throws_does_not_cover_unrelated_type()
    {
        AssertCode(Diags("fn t(): int throws IOError { return mayThrow(); }"), "LYR-SEM0034");
    }

    [Fact]
    public void Untyped_throws_call_needs_catch_all_or_untyped_clause()
    {
        AssertCode(Diags("fn t() { try { mayThrowAny(); } catch (e: NotFound) { } }"), "LYR-SEM0034");
        AssertClean(Diags("fn u(): int throws { return mayThrowAny(); }"));
        AssertClean(Diags("fn v() { try { mayThrowAny(); } catch (_) { } }"));
    }

    // --- Catch-Bodies, Rethrow, defer ---

    [Fact]
    public void Throw_in_catch_body_is_not_caught_by_its_own_try()
    {
        AssertCode(Diags("fn t() { try { safe(); } catch (e) { throw e; } }"), "LYR-SEM0034");
    }

    [Fact]
    public void Rethrow_with_untyped_throws_is_clean()
    {
        AssertClean(Diags("fn t(): int throws { try { return mayThrow(); } catch (e) { throw e; } }"));
    }

    [Fact]
    public void Defer_body_participates_in_propagation()
    {
        AssertCode(Diags("fn t() { defer mayThrow(); }"), "LYR-SEM0034");
        AssertClean(Diags("fn u() throws NotFound { defer mayThrow(); }"));
    }

    // --- Lambdas und Kontext-Grenzen ---

    [Fact]
    public void Lambda_body_is_its_own_context()
    {
        AssertCode(Diags("fn t() { let f = (x: int) => mayThrow(); }"), "LYR-SEM0034");
    }

    [Fact]
    public void Enclosing_try_does_not_protect_lambda_bodies()
    {
        AssertCode(Diags("fn t() { try { let f = (x: int) => mayThrow(); } catch (_) { } }"), "LYR-SEM0034");
    }

    [Fact]
    public void Global_initializer_has_no_handler()
    {
        AssertCode(Diags("let g = mayThrow();"), "LYR-SEM0034");
    }

    // --- throws-Funktion als Wert (SEM0037) ---

    [Fact]
    public void Throws_function_as_value_is_reported()
    {
        AssertCode(Diags("fn t() { let f = mayThrow; }"), "LYR-SEM0037");
    }

    [Fact]
    public void Plain_function_as_value_is_fine()
    {
        AssertClean(Diags("fn t() { let f = safe; }"));
    }

    // --- panic (§9): never divergiert, wirft aber nicht ---

    [Fact]
    public void Panic_counts_as_divergence_for_return_coverage()
    {
        AssertClean(Diags("""fn t(): int { panic("boom"); }"""));
        AssertClean(Diags("""fn u(n: int): int { if (n > 0) { return 1; } panic("unreachable"); }"""));
    }

    [Fact]
    public void Panic_needs_no_throws_declaration()
    {
        AssertClean(Diags("""fn t() { panic("boom"); }"""));
    }
}
