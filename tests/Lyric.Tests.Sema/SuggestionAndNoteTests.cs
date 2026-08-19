using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Xunit;

namespace Lyric.Tests.Sema;

/// <summary>
/// The finer half of the diagnostics milestone: notes on the errors that always had a second
/// place to point at, "did you mean" where a name resolved to nothing, and the never-reassigned
/// hint. A suggestion appears only when it is unique — a wrong guess is worse than no guess —
/// and every note points at code, never at literature.
/// </summary>
public class SuggestionAndNoteTests
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

    private static Diagnostic Single(DiagnosticEngine de, string code) =>
        Assert.Single(de.Diagnostics, d => d.Code == code);

    // ─── did you mean ──────────────────────────────────────────────────────

    [Fact]
    public void An_unknown_identifier_suggests_the_close_name()
    {
        var de = Check("fn main(): int {\n    let count = 1;\n    return coutn;\n}\n");
        var error = Single(de, "LYR-SEM0002");
        var note = Assert.Single(error.Notes!);
        Assert.Equal("did you mean 'count'?", note.Message);
    }

    [Fact]
    public void A_tie_between_two_close_names_suggests_nothing()
    {
        var de = Check(
            "fn main(): int {\n    let abc = 1;\n    let abd = abc;\n    let x = abd;\n    return abx + x;\n}\n");
        var error = Single(de, "LYR-SEM0002");
        Assert.True(error.Notes is null or { Count: 0 },
            "two names at the same distance must not produce a guess");
    }

    [Fact]
    public void A_distant_name_suggests_nothing()
    {
        var de = Check("fn main(): int {\n    let count = 1;\n    return zzz + count;\n}\n");
        var error = Single(de, "LYR-SEM0002");
        Assert.True(error.Notes is null or { Count: 0 });
    }

    [Fact]
    public void An_unknown_type_suggests_the_close_type()
    {
        var de = Check(
            "struct Point { x: int, }\nfn main(): int {\n    let p = Poitn { x = 1 };\n    return p.x;\n}\n");
        var error = Single(de, "LYR-SEM0011");
        var note = Assert.Single(error.Notes!);
        Assert.Equal("did you mean 'Point'?", note.Message);
    }

    [Fact]
    public void An_unknown_member_suggests_the_close_member()
    {
        var de = Check(
            "struct Point { size: int, }\nfn main(): int {\n    let p = Point { size = 1 };\n    return p.sise;\n}\n");
        var error = Single(de, "LYR-SEM0012");
        var note = Assert.Single(error.Notes!);
        Assert.Equal("did you mean 'size'?", note.Message);
    }

    // ─── previous declaration ──────────────────────────────────────────────

    [Fact]
    public void A_duplicate_declaration_points_back_at_the_first()
    {
        var de = Check("fn f(): int { return 1; }\nstruct f { x: int, }\nfn main(): int { return 0; }\n");
        var error = Single(de, "LYR-RES0001");
        var note = Assert.Single(error.Notes!);
        Assert.Equal("previous declaration", note.Message);
        Assert.True(note.Location.File.IsValid);
        Assert.True(note.Location.Start < error.Span.Start,
            "the note points at the FIRST declaration, which stands before the duplicate");
    }

    // ─── the interface member behind a conformance error ───────────────────

    [Fact]
    public void A_missing_interface_method_points_at_its_declaration()
    {
        var de = Check(
            "interface Greet {\n    fn hello(): int;\n}\n"
            + "struct S :: [Greet] { x: int, }\n"
            + "fn main(): int { return 0; }\n");
        var error = Single(de, "LYR-SEM0020");
        var note = Assert.Single(error.Notes!);
        Assert.Contains("'hello' is declared here", note.Message);
        Assert.True(note.Location.File.IsValid);
    }

    // The block-lambda annotation note is gone with its error: since v1.13 the annotation-free
    // form infers its return type (LambdaTests pin the inference itself).

    // ─── the never-reassigned hint (LYR-SEM0075) ───────────────────────────

    [Fact]
    public void A_var_never_reassigned_hints_at_let()
    {
        var de = Check("fn main(): int {\n    var x = 1;\n    return x;\n}\n");
        Assert.False(de.HasErrors);
        var hint = Single(de, "LYR-SEM0075");
        Assert.Equal(Severity.Hint, hint.Severity);
        Assert.Contains("'let' would do", hint.Message);
    }

    [Fact]
    public void A_reassigned_var_is_silent()
    {
        var de = Check("fn main(): int {\n    var x = 1;\n    x = 2;\n    return x;\n}\n");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0075");
    }

    [Fact]
    public void A_compound_assignment_counts_as_reassignment()
    {
        var de = Check("fn main(): int {\n    var x = 1;\n    x += 2;\n    return x;\n}\n");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0075");
    }

    [Fact]
    public void An_increment_counts_as_reassignment()
    {
        var de = Check("fn main(): int {\n    var x = 1;\n    x++;\n    return x;\n}\n");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0075");
    }

    [Fact]
    public void A_reassignment_inside_a_lambda_counts()
    {
        var de = Check(
            "fn main(): int {\n    var x = 1;\n    let bump = (): void => {\n        x = x + 1;\n    };\n    bump();\n    return x;\n}\n");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0075");
    }

    [Fact]
    public void A_field_write_through_the_binding_counts_as_mutation()
    {
        // 'let b; b.x = 99;' happens to COMPILE — but the hint must not advise hiding mutation
        // behind 'let'. A var written through keeps its var.
        var de = Check(
            "struct P { x: int, }\nfn main(): int {\n    var b = P { x = 1 };\n    b.x = 99;\n    return b.x;\n}\n");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0075");
    }

    [Fact]
    public void A_mut_method_call_on_the_binding_counts_as_mutation()
    {
        var de = Check(
            "struct S {\n    x: int,\n    mut fn bump(): void {\n        this.x = this.x + 1;\n    }\n}\n"
            + "fn main(): int {\n    var s = S { x = 1 };\n    s.bump();\n    return s.x;\n}\n");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0075");
    }

    [Fact]
    public void A_reference_typed_var_passed_to_a_call_counts_as_touched()
    {
        // The callee may write the array's elements; the analysis cannot see across the call.
        var de = Check(
            "fn fill(xs: int[]): void {\n    xs[0] = 9;\n}\n"
            + "fn main(): int {\n    var buffer = [0, 0];\n    fill(buffer);\n    return buffer[0];\n}\n");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0075");
    }

    [Fact]
    public void A_scalar_var_passed_to_a_call_still_hints()
    {
        // A value argument is a copy: no callee can change 'x' through it.
        var de = Check(
            "fn twice(n: int): int {\n    return n * 2;\n}\n"
            + "fn main(): int {\n    var x = 1;\n    return twice(x);\n}\n");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0075");
    }

    [Fact]
    public void An_unused_var_gets_the_unused_warning_and_no_hint()
    {
        var de = Check("fn main(): int {\n    var x = 1;\n    return 0;\n}\n");
        Assert.Contains(de.Diagnostics, d => d.Code == "LYR-SEM0071");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0075");
    }

    [Fact]
    public void A_var_declared_without_an_initializer_is_exempt()
    {
        // Its later assignment COMPLETES the declaration; 'let' could not write that program.
        var de = Check(
            "fn main(): int {\n    var x: int;\n    if (true) {\n        x = 1;\n    } else {\n        x = 2;\n    }\n    return x;\n}\n");
        Assert.DoesNotContain(de.Diagnostics, d => d.Code == "LYR-SEM0075");
    }
}
