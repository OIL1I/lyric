using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// The warnings pass: unused locals, unused imports, unreachable statements. Everything here
/// COMPILES — the assertions are about what deserves fixing, never about validity, and every
/// positive case has the negative beside it that keeps the warning from firing at rest.
/// </summary>
public class WarningAnalyzerTests
{
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static DiagnosticEngine Check(string source, bool withStdlib = false)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);

        if (withStdlib)
            comp.ModuleLoader = StdlibLoader.ForRoot(Path.Combine(RepoRoot(), "stdlib"), sm, de);

        comp.AddModule(new Parser(sm, id, de).ParseModule());
        Semantics.Analyze(comp, comp.Resolve(), de);
        return de;
    }

    private static void AssertWarns(DiagnosticEngine de, string code)
    {
        Assert.False(de.HasErrors, "expected a warning on a VALID program, but it has errors:\n"
            + string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Contains(de.Diagnostics, d => d.Code == code && d.Severity == Severity.Warning);
    }

    private static void AssertSilent(DiagnosticEngine de)
    {
        Assert.True(de.Count == 0, "expected no diagnostics at all, but got:\n"
            + string.Join("\n", de.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    // ─── unused locals (LYR-SEM0071) ───────────────────────────────────────

    [Fact]
    public void An_unused_let_warns()
    {
        var de = Check("fn main(): int {\n    let x = 1;\n    return 0;\n}\n");
        AssertWarns(de, "LYR-SEM0071");
        Assert.Contains(de.Diagnostics, d => d.Message.Contains("'x' is never used"));
    }

    [Fact]
    public void A_used_local_is_silent()
    {
        AssertSilent(Check("fn main(): int {\n    let x = 1;\n    return x;\n}\n"));
    }

    [Fact]
    public void An_underscore_binder_is_exempt()
    {
        AssertSilent(Check("fn f(): int { return 1; }\nfn main(): int {\n    let _ = f();\n    return 0;\n}\n"));
    }

    [Fact]
    public void A_write_counts_as_a_reference()
    {
        // 'never used' means never referenced. A write-only variable is referenced — whether the
        // WRITES are pointless is a different analysis, deliberately not this one.
        AssertSilent(Check("fn main(): int {\n    var x = 1;\n    x = 2;\n    return 0;\n}\n"));
    }

    [Fact]
    public void A_capture_counts_as_a_use()
    {
        AssertSilent(Check(
            "fn main(): int {\n    let x = 1;\n    let f = (): int => x;\n    return f();\n}\n"));
    }

    [Fact]
    public void An_unused_loop_variable_warns()
    {
        var de = Check("fn main(): int {\n    var n = 0;\n    for (i in [1, 2]) {\n        n = n + 1;\n    }\n    return n;\n}\n",
            withStdlib: true);
        AssertWarns(de, "LYR-SEM0071");
        Assert.Contains(de.Diagnostics, d => d.Message.Contains("loop variable 'i'"));
    }

    [Fact]
    public void An_unused_catch_binding_warns()
    {
        var de = Check(
            "fn risky(): int throws { return 1; }\n"
            + "fn main(): int {\n    try {\n        return risky();\n    } catch (e) {\n        return 0;\n    }\n}\n",
            withStdlib: true);
        AssertWarns(de, "LYR-SEM0071");
        Assert.Contains(de.Diagnostics, d => d.Message.Contains("catch binding 'e'"));
    }

    [Fact]
    public void A_destructuring_warns_per_name()
    {
        var de = Check("fn main(): int {\n    let (a, b) = (1, 2);\n    return a;\n}\n");
        AssertWarns(de, "LYR-SEM0071");
        Assert.Contains(de.Diagnostics, d => d.Message.Contains("'b' is never used"));
        Assert.DoesNotContain(de.Diagnostics, d => d.Message.Contains("'a' is never used"));
    }

    [Fact]
    public void A_shorthand_field_pattern_is_exempt()
    {
        // 'Rect { w, h }' binds the FIELD'S names, not ones the author chose, and the grammar
        // has no ellipsis to leave a field out — warning here would demand 'w = _' boilerplate
        // for the most idiomatic way to match a variant.
        AssertSilent(Check(
            "enum Shape {\n    Rect { w: int, h: int },\n    Empty;\n"
            + "    fn name(): string {\n        return match (this) {\n"
            + "            Rect { w, h } => \"rect\",\n            Empty => \"empty\",\n        };\n    }\n}\n"
            + "fn main(): int {\n    let s = Shape.Empty;\n    return if (s.name() == \"empty\") 1 else 0;\n}\n",
            withStdlib: true));
    }

    [Fact]
    public void An_explicitly_renamed_field_pattern_still_warns()
    {
        var de = Check(
            "enum Shape {\n    Rect { w: int, h: int },\n    Empty;\n"
            + "    fn wide(): bool {\n        return match (this) {\n"
            + "            Rect { w = width, h = _ } => true,\n            Empty => false,\n        };\n    }\n}\n"
            + "fn main(): int {\n    let s = Shape.Empty;\n    return if (s.wide()) 1 else 0;\n}\n",
            withStdlib: true);
        AssertWarns(de, "LYR-SEM0071");
        Assert.Contains(de.Diagnostics, d => d.Message.Contains("'width' is never used"));
    }

    [Fact]
    public void An_unused_parameter_is_deliberately_exempt()
    {
        // A signature is often not the author's to change: interface conformance and callback
        // shapes fix it. No warning, by decision rather than by gap.
        AssertSilent(Check("fn f(unused: int): int { return 1; }\nfn main(): int { return f(2); }\n"));
    }

    [Fact]
    public void A_broken_program_gets_no_warnings()
    {
        // Over a program with errors the tables are partial, and a warning computed from half a
        // table is a guess. Errors only.
        var de = Check("fn main(): int {\n    let x = 1;\n    return \"no\";\n}\n");
        Assert.True(de.HasErrors);
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0071");
    }

    // ─── unused imports (LYR-SEM0072) ──────────────────────────────────────

    [Fact]
    public void An_unused_import_warns()
    {
        var de = Check("import std.math { pi };\n\nfn main(): int {\n    return 0;\n}\n",
            withStdlib: true);
        AssertWarns(de, "LYR-SEM0072");
        Assert.Contains(de.Diagnostics, d => d.Message.Contains("import 'pi'"));
    }

    [Fact]
    public void A_used_import_is_silent()
    {
        AssertSilent(Check(
            "import std.string { concat, parseInt };\n\nfn main(): int {\n    return parseInt(concat(\"4\", \"2\")) ?? 0;\n}\n",
            withStdlib: true));
    }

    [Fact]
    public void Only_the_unused_name_of_a_clause_warns()
    {
        var de = Check(
            "import std.string { concat, repeat };\n\nfn main(): int {\n    return concat(\"a\", \"b\").length();\n}\n",
            withStdlib: true);
        AssertWarns(de, "LYR-SEM0072");
        Assert.Contains(de.Diagnostics, d => d.Message.Contains("import 'repeat'"));
        Assert.DoesNotContain(de.Diagnostics, d => d.Message.Contains("import 'concat'"));
    }

    [Fact]
    public void A_type_used_only_in_an_annotation_counts_as_used()
    {
        // The use stands in a type position, not in an expression: the resolver's table carries
        // it, and the analysis reads both tables for exactly this case.
        AssertSilent(Check(
            "import std.core { Exception };\n\n"
            + "fn describe(e: Exception): string {\n    return e.message();\n}\n"
            + "fn main(): int {\n    return 0;\n}\n",
            withStdlib: true));
    }

    // ─── builtin-shadowing imports (LYR-SEM0077) ───────────────────────────

    [Fact]
    public void A_bare_import_binding_a_builtin_type_name_warns()
    {
        var de = Check("import std.string;\n\nfn main(): int {\n    return 0;\n}\n",
            withStdlib: true);
        AssertWarns(de, "LYR-SEM0077");
        Assert.Contains(de.Diagnostics, d => d.Message.Contains("shadowing the builtin type"));
    }

    [Fact]
    public void A_selective_import_of_the_same_module_is_silent()
    {
        AssertSilent(Check(
            "import std.string { parseInt };\n\nfn main(): int {\n    return parseInt(\"ab\") ?? 0;\n}\n",
            withStdlib: true));
    }

    [Fact]
    public void Using_the_shadowed_type_as_an_annotation_is_the_module_error()
    {
        // The crash this replaced: the local-annotation path produced an ErrorType WITHOUT
        // reporting, and the lowering threw on it. Now it is LYR-SEM0011 naming the trap.
        var de = Check(
            "import std.string;\n\nfn main(): int {\n    let s: string = \"x\";\n    return 0;\n}\n",
            withStdlib: true);
        Assert.True(de.HasErrors);
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0011"
            && d.Message.Contains("is a module, not a type"));
    }

    // ─── unreachable statements (LYR-SEM0073) ──────────────────────────────

    [Fact]
    public void A_statement_after_return_warns()
    {
        var de = Check("fn main(): int {\n    return 1;\n    return 2;\n}\n");
        AssertWarns(de, "LYR-SEM0073");
    }

    [Fact]
    public void The_note_points_at_the_exit()
    {
        var de = Check("fn main(): int {\n    return 1;\n    return 2;\n}\n");
        var warning = Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0073");
        var note = Assert.Single(warning.Notes!);
        Assert.Contains("leaves the block", note.Message);
        Assert.True(note.Location.File.IsValid);
    }

    [Fact]
    public void One_report_per_block_not_one_per_statement()
    {
        var de = Check(
            "fn main(): int {\n    return 1;\n    return 2;\n    return 3;\n    return 4;\n}\n");
        Assert.Single(de.Diagnostics, d => d.Code == "LYR-SEM0073");
    }

    [Fact]
    public void A_statement_after_break_warns()
    {
        var de = Check(
            "fn main(): int {\n    while (true) {\n        break;\n        return 1;\n    }\n    return 0;\n}\n");
        AssertWarns(de, "LYR-SEM0073");
    }

    [Fact]
    public void Code_after_an_if_whose_branches_both_return_warns()
    {
        var de = Check(
            "fn main(): int {\n    if (true) {\n        return 1;\n    } else {\n        return 2;\n    }\n    return 3;\n}\n");
        AssertWarns(de, "LYR-SEM0073");
    }

    [Fact]
    public void An_if_without_else_is_silent_afterwards()
    {
        AssertSilent(Check(
            "fn main(): int {\n    if (true) {\n        return 1;\n    }\n    return 0;\n}\n"));
    }

    [Fact]
    public void A_lambda_body_is_walked_too()
    {
        var de = Check(
            "fn main(): int {\n    let f = (): int => {\n        return 1;\n        return 2;\n    };\n    return f();\n}\n");
        AssertWarns(de, "LYR-SEM0073");
    }

    // ─── duplicate modules (LYR-RES0007) ───────────────────────────────────

    [Fact]
    public void Two_files_claiming_one_module_name_is_an_error_with_a_note()
    {
        var sm = new SourceManager();
        var first = sm.AddVirtual("a.lyr", "module dup;\n\npub fn one(): int { return 1; }\n");
        var second = sm.AddVirtual("b.lyr", "module dup;\n\npub fn two(): int { return 2; }\n");
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);

        comp.AddModule(new Parser(sm, first, de).ParseModule());
        comp.AddModule(new Parser(sm, second, de).ParseModule());

        var error = Assert.Single(de.Diagnostics, d => d.Code == "LYR-RES0007");
        Assert.Equal(Severity.Error, error.Severity);
        Assert.Equal(second, error.Span.File);

        var note = Assert.Single(error.Notes!);
        Assert.Equal(first, note.Location.File);
    }
}
