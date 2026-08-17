using System.Runtime.CompilerServices;
using System.Text.Json;
using Lyric.Compiler;
using Lyric.Lsp.Analysis;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// What can follow a <c>.</c>.
///
/// <para>The cursor is marked with <c>$</c> and the marker is stripped before the text is handed
/// over — the fixture shows a program in the state an editor is in, which for this feature is a
/// state that does not parse.</para>
/// </summary>
public sealed class CompletionTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static IReadOnlyList<CompletionItem>? Complete(string programWithCursor)
    {
        var offset = programWithCursor.IndexOf('$');
        Assert.True(offset >= 0, "the fixture has no '$' marking the cursor");

        var program = programWithCursor.Remove(offset, 1);
        var path = Path.Combine(AppContext.BaseDirectory, "completion.lyr");

        return CompletionProvider.At(path, program, offset, new CompilerOptions
        {
            StdlibRoot = Path.Combine(RepoRoot(), "stdlib"),
        });
    }

    private static string[] Labels(string programWithCursor)
    {
        var items = Complete(programWithCursor);
        Assert.NotNull(items);
        return items.Select(i => i.Label).ToArray();
    }

    // ------------------------------------------------------------------ the four sources

    [Fact]
    public void A_field_is_offered_on_an_instance()
    {
        var labels = Labels(
            "struct Point { x: int, y: int, }\n"
            + "fn main(): int {\n    let p = Point { x = 1, y = 2 };\n    return p.$;\n}\n");

        Assert.Contains("x", labels);
        Assert.Contains("y", labels);
    }

    [Fact]
    public void An_instance_method_is_offered()
    {
        var labels = Labels(
            "struct Point {\n    x: int,\n    fn doubled(): int { return this.x * 2; }\n}\n"
            + "fn main(): int {\n    let p = Point { x = 1 };\n    return p.$;\n}\n");

        Assert.Contains("doubled", labels);
    }

    [Fact]
    public void An_extension_method_is_offered()
    {
        // One of the two things a list assembled in the server would miss.
        var labels = Labels(
            "struct Point { x: int, }\n"
            + "extend Point {\n    fn tripled(): int { return this.x * 3; }\n}\n"
            + "fn main(): int {\n    let p = Point { x = 1 };\n    return p.$;\n}\n");

        Assert.Contains("tripled", labels);
        Assert.Contains("x", labels);
    }

    [Fact]
    public void An_interface_default_method_is_offered()
    {
        // The other one. It is declared on neither the type nor an extension.
        var labels = Labels(
            "interface Greets {\n    fn name(): int;\n    fn greet(): int { return this.name() + 1; }\n}\n"
            + "struct P :: [Greets] {\n    v: int,\n    fn name(): int { return this.v; }\n}\n"
            + "fn main(): int {\n    let p = P { v = 1 };\n    return p.$;\n}\n");

        Assert.Contains("greet", labels);
        Assert.Contains("name", labels);
    }

    [Fact]
    public void A_string_offers_the_extensions_of_the_standard_library()
    {
        // A primitive has no members of its own. Everything a string can do is an extension, so a
        // list without them would be empty exactly where completion is used most.
        var labels = Labels("fn main(): int {\n    let s = \"abc\";\n    let n = s.$;\n    return 0;\n}\n");

        Assert.NotEmpty(labels);
    }

    // ------------------------------------------------------------------ the receiver decides

    [Fact]
    public void A_type_name_offers_the_static_side_only()
    {
        var labels = Labels(
            "struct Point {\n    x: int,\n    static fn origin(): int { return 0; }\n}\n"
            + "fn main(): int { return Point.$; }\n");

        Assert.Contains("origin", labels);
        Assert.DoesNotContain("x", labels);
    }

    [Fact]
    public void A_value_offers_the_instance_side_only()
    {
        var labels = Labels(
            "struct Point {\n    x: int,\n    static fn origin(): int { return 0; }\n}\n"
            + "fn main(): int {\n    let p = Point { x = 1 };\n    return p.$;\n}\n");

        Assert.Contains("x", labels);
        Assert.DoesNotContain("origin", labels);
    }

    [Fact]
    public void An_enum_offers_its_variants_on_the_type()
    {
        var labels = Labels(
            "enum Shape {\n    Circle(float),\n    Square(float),\n}\n"
            + "fn main(): int {\n    let s = Shape.$;\n    return 0;\n}\n");

        Assert.Contains("Circle", labels);
        Assert.Contains("Square", labels);
    }

    [Fact]
    public void A_module_offers_what_it_exports()
    {
        // A namespace import binds the LAST segment, so the receiver is 'os' and not 'std.os'.
        var labels = Labels("import std.os;\nfn main(): int {\n    return os.$;\n}\n");

        Assert.Contains("cpuCount", labels);
    }

    // ------------------------------------------------------------------ counter-checks

    [Fact]
    public void A_position_that_is_not_a_member_access_offers_nothing()
    {
        // Names in scope are slice 3. Answering here with every name would be a different feature
        // wearing this one's trigger.
        Assert.Null(Complete("fn main(): int {\n    let x = 1;\n    return $x;\n}\n"));
    }

    [Fact]
    public void No_label_contains_the_marker()
    {
        // The synthetic identifier exists for one compile and must not reach a user. It cannot leak
        // through diagnostics — this request publishes none — so the labels are the remaining way.
        var labels = Labels(
            "struct Point { x: int, }\n"
            + "fn main(): int {\n    let p = Point { x = 1 };\n    return p.$;\n}\n");

        Assert.All(labels, label => Assert.DoesNotContain("lyric_completion", label));
    }

    [Fact]
    public void A_cursor_inside_a_half_typed_name_still_offers_the_members()
    {
        // 'p.x' parses on its own, so this case never needed the marker — but it goes through the
        // same path, and inserting into the middle of the name must not change which receiver is
        // asked about.
        var labels = Labels(
            "struct Point { x: int, extra: int, }\n"
            + "fn main(): int {\n    let p = Point { x = 1, extra = 2 };\n    return p.e$;\n}\n");

        Assert.Contains("extra", labels);
        Assert.Contains("x", labels);
    }

    [Fact]
    public void A_file_with_type_errors_still_offers_the_members()
    {
        var labels = Labels(
            "struct Point { x: int, }\n"
            + "fn main(): int {\n    let bad = \"s\" + nowhere;\n"
            + "    let p = Point { x = 1 };\n    return p.$;\n}\n");

        Assert.Contains("x", labels);
    }

    // ------------------------------------------------------------------ what an item carries

    [Fact]
    public void An_item_carries_the_documentation_written_above_it()
    {
        var items = Complete(
            "struct Point {\n    /// How far to the right.\n    x: int,\n}\n"
            + "fn main(): int {\n    let p = Point { x = 1 };\n    return p.$;\n}\n");

        Assert.NotNull(items);
        var field = Assert.Single(items, i => i.Label == "x");
        Assert.Equal("How far to the right.", field.Documentation?.Value);
    }

    [Fact]
    public void An_item_says_where_it_came_from()
    {
        var items = Complete(
            "struct Point { x: int, }\n"
            + "extend Point {\n    fn tripled(): int { return this.x * 3; }\n}\n"
            + "fn main(): int {\n    let p = Point { x = 1 };\n    return p.$;\n}\n");

        Assert.NotNull(items);
        Assert.Equal("extension on Point", Assert.Single(items, i => i.Label == "tripled").Detail);
        Assert.Equal("Point", Assert.Single(items, i => i.Label == "x").Detail);
    }

    [Fact]
    public void A_field_and_a_method_get_different_kinds()
    {
        var items = Complete(
            "struct Point {\n    x: int,\n    fn doubled(): int { return this.x * 2; }\n}\n"
            + "fn main(): int {\n    let p = Point { x = 1 };\n    return p.$;\n}\n");

        Assert.NotNull(items);
        Assert.Equal(CompletionItemKind.Field, Assert.Single(items, i => i.Label == "x").Kind);
        Assert.Equal(CompletionItemKind.Method, Assert.Single(items, i => i.Label == "doubled").Kind);
    }
}

/// <summary>Completion over the wire.</summary>
public sealed class CompletionProtocolTests
{
    private const string Program =
        "struct Point { x: int, }\nfn main(): int {\n    let p = Point { x = 1 };\n    return p.;\n}\n";

    private static string BufferPath([CallerMemberName] string name = "") =>
        Path.Combine(AppContext.BaseDirectory, $"{name}.lyr");

    [Fact]
    public async Task The_capability_and_its_trigger_character_are_announced()
    {
        await using var harness = new ServerHarness();

        var id = await harness.RequestAsync(LspMethods.Initialize, "{}");
        var response = await harness.ReceiveResponseAsync(id);

        var completion = response.GetProperty("result").GetProperty("capabilities")
            .GetProperty("completionProvider");

        Assert.Equal(".", completion.GetProperty("triggerCharacters")[0].GetString());
    }

    [Fact]
    public async Task The_members_come_back_as_items()
    {
        var uri = DocumentUri.FromFilePath(BufferPath());

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();
        await harness.NotifyAsync(LspMethods.DidOpen, JsonSerializer.Serialize(new
        {
            textDocument = new { uri, languageId = "lyric", version = 1, text = Program },
        }));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        // Line 3, right after the '.'.
        var id = await harness.RequestAsync(LspMethods.Completion, JsonSerializer.Serialize(new
        {
            textDocument = new { uri },
            position = new { line = 3, character = 13 },
        }));

        var result = (await harness.ReceiveResponseAsync(id)).GetProperty("result");

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Contains(result.EnumerateArray(), item => item.GetProperty("label").GetString() == "x");
    }

    [Fact]
    public async Task A_document_that_was_never_opened_answers_null()
    {
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        var id = await harness.RequestAsync(LspMethods.Completion, JsonSerializer.Serialize(new
        {
            textDocument = new { uri = DocumentUri.FromFilePath(BufferPath()) },
            position = new { line = 0, character = 0 },
        }));

        Assert.Equal(JsonValueKind.Null,
            (await harness.ReceiveResponseAsync(id)).GetProperty("result").ValueKind);
    }
}
