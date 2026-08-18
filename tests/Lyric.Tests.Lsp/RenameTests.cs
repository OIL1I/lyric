using Lyric.AST;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Lsp.Analysis;
using Lyric.Lsp.Documents;

namespace Lyric.Tests.Lsp;

/// <summary>
/// Renaming: every place the name stands, and only places the name stands.
///
/// <para>The edits are asserted as TEXTS — every edit's span must slice to exactly the old name.
/// An edit that covers more than the name corrupts the program when the new name replaces it, so
/// this property IS the feature.</para>
/// </summary>
public sealed class RenameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-lsp-rename-" + Guid.NewGuid().ToString("N")[..8]);

    public RenameTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "lyric.json"), """{ "sourceRoot": "src" }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, "src", name);
        File.WriteAllText(path, text);
        return path;
    }

    private CompileResult Check(params string[] paths) =>
        SourceCompiler.CheckProject(
            paths.Select(p => ScriptSource.FromDisk(
                p, Path.GetFileNameWithoutExtension(p))).ToArray(),
            new CompilerOptions { SourceRoot = Path.Combine(_dir, "src") });

    private static Module RootOf(SemanticModel model, FileId file)
    {
        foreach (var module in model.Compilation.Modules)
        {
            var ast = model.Compilation.AstOf(module);
            if (ast.Span.File == file) return ast;
        }

        throw new InvalidOperationException("no module was parsed from this file");
    }

    private static (IReadOnlyList<ReferenceSite>? Edits, string? Refusal) RenameAt(
        CompileResult result, string path, int offset, string newName, bool projectWide = true)
    {
        Assert.NotNull(result.Model);
        var file = DiagnosticMapper.FindFile(result.Sources, path);
        Assert.True(file.IsValid);

        return RenameProvider.Rename(
            result.Model, RootOf(result.Model, file), file, offset, newName, projectWide);
    }

    /// <summary>Every edit as the text its span covers today — each must be the old name.</summary>
    private static string[] Texts(CompileResult result, IEnumerable<ReferenceSite> edits) =>
        edits
            .Select(e => result.Sources.GetText(e.File).Substring(e.Span.Start, e.Span.Length))
            .ToArray();

    // ------------------------------------------------------------------ across files

    [Fact]
    public void A_function_rename_reaches_the_importing_file_and_its_import_clause()
    {
        // The headline: the declaration, the call in ANOTHER file, and the name inside
        // 'import util { value }' — the clause declares a binding rather than using the target, so
        // no reference table carries it, and forgetting it breaks every importer.
        var utilText = "module util;\n\npub fn value(): int { return 1; }\n";
        var appText = "import util { value };\n\nfn main(): int { return value(); }\n";
        var util = Write("util.lyr", utilText);
        var app = Write("app.lyr", appText);

        var result = Check(app, util);
        var (edits, refusal) = RenameAt(result, util,
            utilText.IndexOf("value", StringComparison.Ordinal) + 1, "amount");

        Assert.Null(refusal);
        Assert.NotNull(edits);
        Assert.Equal(3, edits.Count);
        Assert.All(Texts(result, edits), text => Assert.Equal("value", text));

        // Two files are touched: the rename is exactly as project-wide as the compilation.
        Assert.Equal(2, edits.Select(e => e.File).Distinct().Count());
    }

    [Fact]
    public void A_struct_rename_covers_annotation_initializer_and_import()
    {
        var shapesText = "module shapes;\n\npub struct Point { x: int, }\n";
        var appText = "import shapes { Point };\n\n"
            + "fn main(): int {\n    let p: Point = Point { x = 1 };\n    return p.x;\n}\n";
        var shapes = Write("shapes.lyr", shapesText);
        var app = Write("app.lyr", appText);

        var result = Check(app, shapes);
        var (edits, refusal) = RenameAt(result, shapes,
            shapesText.IndexOf("Point", StringComparison.Ordinal) + 1, "Vec");

        Assert.Null(refusal);
        Assert.NotNull(edits);

        // Declaration, import clause, annotation, initializer head — and each is the NAME, not
        // the initializer or the generic form around it.
        Assert.Equal(4, edits.Count);
        Assert.All(Texts(result, edits), text => Assert.Equal("Point", text));
    }

    [Fact]
    public void A_field_rename_covers_access_and_initializer_field()
    {
        var text = "struct Point { x: int, }\n"
            + "fn main(): int {\n    let p = Point { x = 1 };\n    return p.x;\n}\n";
        var app = Write("app.lyr", text);

        var result = Check(app);
        var (edits, refusal) = RenameAt(result, app,
            text.IndexOf("x: int", StringComparison.Ordinal), "width");

        Assert.Null(refusal);
        Assert.NotNull(edits);
        Assert.Equal(3, edits.Count);
        Assert.All(Texts(result, edits), edit => Assert.Equal("x", edit));
    }

    // ------------------------------------------------------------------ the operator seam

    [Fact]
    public void An_operator_use_is_not_an_edit()
    {
        // 'a + b' calls 'add' through the conformance, and the sema records that call on a node it
        // synthesized. The rename must edit the declaration and the WRITTEN call — and must not
        // touch the operator expression, whose text contains no 'add' to replace.
        var text = "import std.core { Add };\n\n"
            + "struct Vec :: [Add<Vec>] {\n"
            + "    x: int,\n"
            + "    fn add(other: Vec): Vec {\n"
            + "        return Vec { x = this.x + other.x };\n"
            + "    }\n"
            + "}\n\n"
            + "fn main(): int {\n"
            + "    let a = Vec { x = 1 };\n"
            + "    let b = Vec { x = 2 };\n"
            + "    let c = a + b;\n"
            + "    let d = a.add(b);\n"
            + "    return c.x + d.x;\n"
            + "}\n";
        var app = Write("app.lyr", text);

        var result = Check(app);
        Assert.True(result.Ok, string.Join("\n",
            result.Diagnostics.SortedSnapshot().Select(d => $"{d.Code}: {d.Message}")));

        var (edits, refusal) = RenameAt(result, app,
            text.IndexOf("fn add", StringComparison.Ordinal) + 4, "plus");

        Assert.Null(refusal);
        Assert.NotNull(edits);
        Assert.Equal(2, edits.Count); // the declaration and 'a.add(b)' — nothing at 'a + b'
        Assert.All(Texts(result, edits), edit => Assert.Equal("add", edit));
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public void The_standard_library_is_not_renamed_from_here()
    {
        var text = "import std.io.console { println };\n\n"
            + "fn main(): int {\n    println(\"hi\");\n    return 0;\n}\n";
        var app = Write("app.lyr", text);

        var result = Check(app);
        var (edits, refusal) = RenameAt(result, app,
            text.IndexOf("println(\"hi\")", StringComparison.Ordinal) + 1, "shout");

        Assert.Null(edits);
        Assert.Contains("outside this project", refusal);
    }

    [Fact]
    public void A_keyword_is_not_a_new_name()
    {
        var text = "fn value(): int { return 1; }\nfn main(): int { return value(); }\n";
        var app = Write("app.lyr", text);

        var result = Check(app);

        foreach (var illegal in new[] { "fn", "my name", "3rd", "@tag", "" })
        {
            var (edits, refusal) = RenameAt(result, app, text.IndexOf("fn value",
                StringComparison.Ordinal) + 4, illegal);

            Assert.Null(edits);
            Assert.Contains("identifier", refusal);
        }
    }

    [Fact]
    public void A_module_is_refused_with_its_reason()
    {
        var utilText = "module util;\n\npub fn value(): int { return 1; }\n";
        var appText = "import util;\n\nfn main(): int { return util.value(); }\n";
        var util = Write("util.lyr", utilText);
        var app = Write("app.lyr", appText);

        var result = Check(app, util);
        var (edits, refusal) = RenameAt(result, app,
            appText.IndexOf("return util", StringComparison.Ordinal) + 8, "tools");

        Assert.Null(edits);
        Assert.Contains("module", refusal);
    }

    [Fact]
    public void Outside_a_project_a_rename_must_stay_in_the_file()
    {
        // The compilation of a lone file is rooted at that file, and files it cannot see may
        // import it: a cross-file edit set cannot be trusted to be complete, so it is refused. A
        // rename that stays inside the file is safe and allowed.
        var loose = Path.Combine(_dir, "loose");
        Directory.CreateDirectory(loose);
        var utilPath = Path.Combine(loose, "util.lyr");
        var appPath = Path.Combine(loose, "app.lyr");
        File.WriteAllText(utilPath, "module util;\n\npub fn value(): int { return 1; }\n");
        var appText = "import util { value };\n\nfn main(): int {\n"
            + "    let total = value();\n    return total;\n}\n";
        File.WriteAllText(appPath, appText);

        var result = SourceCompiler.Check(appPath, new CompilerOptions());
        Assert.NotNull(result.Model);
        var file = DiagnosticMapper.FindFile(result.Sources, appPath);

        // Renaming the imported function would edit util.lyr: refused.
        var (crossEdits, crossRefusal) = RenameProvider.Rename(result.Model,
            result.Model.Entry, file,
            appText.IndexOf("value()", StringComparison.Ordinal) + 1, "amount",
            projectWide: false);
        Assert.Null(crossEdits);
        Assert.Contains("lyric.json", crossRefusal);

        // Renaming the local stays in the file: allowed.
        var (localEdits, localRefusal) = RenameProvider.Rename(result.Model,
            result.Model.Entry, file,
            appText.IndexOf("total", StringComparison.Ordinal) + 1, "sum",
            projectWide: false);
        Assert.Null(localRefusal);
        Assert.NotNull(localEdits);
        Assert.Equal(2, localEdits.Count);
    }

    // ------------------------------------------------------------------ prepare

    [Fact]
    public void Prepare_answers_the_name_range_and_placeholder()
    {
        var text = "fn value(): int { return 1; }\nfn main(): int { return value(); }\n";
        var app = Write("app.lyr", text);

        var result = Check(app);
        Assert.NotNull(result.Model);
        var file = DiagnosticMapper.FindFile(result.Sources, app);

        var (range, refusal) = RenameProvider.Prepare(result.Model,
            RootOf(result.Model, file), file,
            text.IndexOf("return value", StringComparison.Ordinal) + 8, projectWide: true);

        Assert.Null(refusal);
        Assert.NotNull(range);
        Assert.Equal("value", range.Placeholder);
        Assert.Equal("value",
            result.Sources.GetText(range.Span.File)
                .Substring(range.Span.Start, range.Span.Length));
    }
}
