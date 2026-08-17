using System.Text.Json;
using Lyric.Compiler;
using Lyric.Lsp.Analysis;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// The documentation a declaration carries, under its signature.
///
/// <para>The signature half is covered by <see cref="HoverTests"/>; what these fix is that the doc
/// block arrives, that it arrives from OTHER modules too, and that a program without documentation
/// is answered exactly as it was before any of this existed.</para>
/// </summary>
public sealed class HoverDocumentationTests
{
    private static HoverResult? HoverAt(string programWithMarker)
    {
        var offset = programWithMarker.IndexOf('$');
        Assert.True(offset >= 0, "the fixture has no '$' marking the cursor");

        var program = programWithMarker.Remove(offset, 1);
        var path = Path.Combine(AppContext.BaseDirectory, "hover-docs.lyr");

        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, program));
        Assert.NotNull(result.Model);

        var file = DiagnosticMapper.FindFile(result.Sources, path);
        return HoverProvider.At(result.Model, file, offset);
    }

    private static string Text(string programWithMarker)
    {
        var found = HoverAt(programWithMarker);
        Assert.NotNull(found);
        return found.Markdown;
    }

    // ------------------------------------------------------------------ it arrives

    [Fact]
    public void A_documented_function_shows_its_block()
    {
        var text = Text(
            "/// Doubles what it is given.\nfn twice(n: int): int { return n * 2; }\n"
            + "fn main(): int {\n    return tw$ice(2);\n}\n");

        Assert.Contains("fn twice", text);
        Assert.Contains("Doubles what it is given.", text);
    }

    [Fact]
    public void The_signature_comes_first_and_the_block_below_it()
    {
        // The order is the point: a reader looking something up wants the shape before the prose,
        // and the separator is what stops the two running together in a rendered tooltip.
        var text = Text(
            "/// Doubles what it is given.\nfn twice(n: int): int { return n * 2; }\n"
            + "fn main(): int {\n    return tw$ice(2);\n}\n");

        var signature = text.IndexOf("fn twice", StringComparison.Ordinal);
        var separator = text.IndexOf("---", StringComparison.Ordinal);
        var prose = text.IndexOf("Doubles", StringComparison.Ordinal);

        Assert.True(signature < separator && separator < prose, text);
    }

    [Fact]
    public void A_documented_type_shows_its_block()
    {
        var text = Text(
            "/// A point in the plane.\nstruct Point { x: int, y: int, }\n"
            + "fn main(): int {\n    let p: Po$int = Point { x = 1, y = 2 };\n    return p.x;\n}\n");

        Assert.Contains("struct Point", text);
        Assert.Contains("A point in the plane.", text);
    }

    [Fact]
    public void A_documented_field_shows_its_block()
    {
        var text = Text(
            "struct Point {\n    /// How far to the right.\n    x: int,\n    y: int,\n}\n"
            + "fn main(): int {\n    let p = Point { x = 1, y = 2 };\n    return p.$x;\n}\n");

        Assert.Contains("How far to the right.", text);
    }

    [Fact]
    public void A_documented_global_shows_its_block()
    {
        var text = Text(
            "/// The answer.\nlet answer = 42;\nfn main(): int {\n    return ans$wer;\n}\n");

        Assert.Contains("The answer.", text);
    }

    [Fact]
    public void Several_lines_arrive_as_several_lines()
    {
        // The block is joined by the lexer and passed through untouched. A test that only asked for
        // the first line would pass on an implementation that dropped every line after it.
        var text = Text(
            "/// First.\n/// Second.\nfn f(): int { return 1; }\n"
            + "fn main(): int {\n    return $f();\n}\n");

        Assert.Contains("First.\nSecond.", text);
    }

    [Fact]
    public void Markdown_in_a_block_is_not_escaped()
    {
        // What someone writes in a doc comment is what the editor renders. There is no doc-comment
        // vocabulary in the grammar, so there is nothing here to interpret.
        var text = Text(
            "/// Calls `twice` and returns **that**.\nfn f(): int { return 1; }\n"
            + "fn main(): int {\n    return $f();\n}\n");

        Assert.Contains("Calls `twice` and returns **that**.", text);
    }

    // ------------------------------------------------------------------ across modules

    [Fact]
    public void A_standard_library_function_shows_the_block_written_in_its_own_file()
    {
        // The test the whole slice exists for: the block stands in stdlib/std/os.lyr, which the
        // entry module never sees. Without the table crossing the module boundary this is silent.
        var text = Text(
            "import std.os { cpuCount };\nfn main(): int {\n    return cpuC$ount();\n}\n");

        Assert.Contains("fn cpuCount", text);
        Assert.Contains("How many cores the machine has", text);
    }

    [Fact]
    public void An_imported_name_shows_the_documentation_of_what_it_imports()
    {
        // The import binding is not where the block was written. Hover redirects to the target, the
        // same way the jump does.
        var text = Text(
            "import std.os { cpuCount };\nfn main(): int {\n    return cpu$Count();\n}\n");

        Assert.Contains("How many cores the machine has", text);
    }

    // ------------------------------------------------------------------ counter-checks

    [Fact]
    public void A_declaration_without_a_block_is_answered_exactly_as_before()
    {
        // The condition for calling this additive. A separator with nothing under it would be a
        // visible change to every program that has no documentation, which is most of them.
        var text = Text("fn twice(n: int): int { return n * 2; }\n"
            + "fn main(): int {\n    return tw$ice(2);\n}\n");

        Assert.DoesNotContain("---", text);
        Assert.Equal("```lyric\nfn twice(int) -> int\n```", text);
    }

    [Fact]
    public void A_block_separated_by_a_blank_line_belongs_to_nothing()
    {
        // The rule lives in TokenBuffer and this slice must not quietly acquire a second one. A
        // block with a blank line under it is a comment about the file, not about what follows.
        var text = Text(
            "/// Not about the function.\n\nfn twice(n: int): int { return n * 2; }\n"
            + "fn main(): int {\n    return tw$ice(2);\n}\n");

        Assert.DoesNotContain("Not about the function.", text);
    }

    [Fact]
    public void An_ordinary_comment_is_not_documentation()
    {
        var text = Text(
            "// Just a note.\nfn twice(n: int): int { return n * 2; }\n"
            + "fn main(): int {\n    return tw$ice(2);\n}\n");

        Assert.DoesNotContain("Just a note.", text);
    }

    [Fact]
    public void A_subexpression_with_no_declaration_shows_no_documentation()
    {
        // A literal has a type and no declaration. Falling back to the enclosing declaration's block
        // would attach prose to something it was not written about.
        var text = Text("/// Doubles.\nfn twice(n: int): int { return n * $2; }\n");

        Assert.DoesNotContain("Doubles.", text);
    }

    // ------------------------------------------------------------------ the table itself

    [Fact]
    public void The_documentation_of_two_modules_does_not_collide()
    {
        // The reason the table is keyed by node identity. Both files carry a documented declaration
        // at the SAME source offset, which is what an offset-keyed table cannot tell apart — and it
        // would answer with whichever module was read last.
        const string program = "import std.os { cpuCount };\n"
            + "/// Local one.\nfn cores(): int { return 1; }\n"
            + "fn main(): int {\n    return cores() + cpuCount();\n}\n";

        var path = Path.Combine(AppContext.BaseDirectory, "collision.lyr");
        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, program));
        Assert.NotNull(result.Model);

        var file = DiagnosticMapper.FindFile(result.Sources, path);
        var found = HoverProvider.At(
            result.Model, file, program.IndexOf("return cores()", StringComparison.Ordinal) + 9);

        Assert.NotNull(found);
        Assert.Contains("Local one.", found.Markdown);
        Assert.DoesNotContain("How many cores", found.Markdown);
    }

    [Fact]
    public void The_standard_library_contributes_to_the_table()
    {
        // A count rather than a single lookup: it fails loudly if the loader side of the seam is
        // wired up for one module and not for the rest.
        var path = Path.Combine(AppContext.BaseDirectory, "table.lyr");
        var result = SourceCompiler.Check(ScriptSource.FromBuffer(
            path, "import std.os { cpuCount };\nfn main(): int {\n    return cpuCount();\n}\n"));

        Assert.NotNull(result.Model);
        Assert.True(result.Model.Documentation.Count > 10,
            $"only {result.Model.Documentation.Count} documented declarations reached the model");
    }
}

/// <summary>Documentation over the wire, in the member a client actually renders.</summary>
public sealed class HoverDocumentationProtocolTests
{
    private const string Program =
        "/// Doubles what it is given.\nfn twice(n: int): int { return n * 2; }\n"
        + "fn main(): int {\n    return twice(2);\n}\n";

    [Fact]
    public async Task The_block_reaches_the_client_in_the_same_markdown_as_the_signature()
    {
        var uri = DocumentUri.FromFilePath(Path.Combine(AppContext.BaseDirectory, "wire-docs.lyr"));

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, JsonSerializer.Serialize(new
        {
            textDocument = new { uri, languageId = "lyric", version = 1, text = Program },
        }));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        // Line 3, on 'twice'.
        var id = await harness.RequestAsync(LspMethods.Hover, JsonSerializer.Serialize(new
        {
            textDocument = new { uri },
            position = new { line = 3, character = 12 },
        }));

        var contents = (await harness.ReceiveResponseAsync(id))
            .GetProperty("result").GetProperty("contents");

        Assert.Equal("markdown", contents.GetProperty("kind").GetString());

        var value = contents.GetProperty("value").GetString();
        Assert.NotNull(value);
        Assert.Contains("fn twice", value);
        Assert.Contains("Doubles what it is given.", value);
    }
}
