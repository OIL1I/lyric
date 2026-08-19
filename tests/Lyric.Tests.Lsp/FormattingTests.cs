using System.Text.Json;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// <c>textDocument/formatting</c> over the wire: one whole-document edit carrying the
/// formatter's shape, an empty list when the buffer already has it, and NO edits for a buffer
/// that does not parse — the same duty <c>lyrfmt</c> has on disk, behind the editor's gesture.
/// </summary>
public sealed class FormattingTests
{
    private static string Uri(string name) =>
        DocumentUri.FromFilePath(Path.Combine(AppContext.BaseDirectory, name));

    private static async Task<ServerHarness> OpenAsync(string uri, string text)
    {
        var harness = new ServerHarness();
        await harness.InitializeAsync();
        await harness.NotifyAsync(LspMethods.DidOpen, JsonSerializer.Serialize(new
        {
            textDocument = new { uri, languageId = "lyric", version = 1, text },
        }));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);
        return harness;
    }

    private static Task<int> RequestFormattingAsync(ServerHarness harness, string uri) =>
        harness.RequestAsync(LspMethods.Formatting, JsonSerializer.Serialize(new
        {
            textDocument = new { uri },
            options = new { tabSize = 3, insertSpaces = false }, // read for nothing, on purpose
        }));

    [Fact]
    public async Task The_capability_is_announced()
    {
        await using var harness = new ServerHarness();
        var id = await harness.RequestAsync(LspMethods.Initialize, "{}");
        var response = await harness.ReceiveResponseAsync(id);

        Assert.True(response.GetProperty("result").GetProperty("capabilities")
            .GetProperty("documentFormattingProvider").GetBoolean());
    }

    [Fact]
    public async Task A_messy_buffer_gets_one_whole_document_edit()
    {
        var uri = Uri("fmt-messy.lyr");
        await using var harness = await OpenAsync(uri, "fn   main( ):int{return   0;}");

        var id = await RequestFormattingAsync(harness, uri);
        var response = await harness.ReceiveResponseAsync(id);

        var edit = Assert.Single(response.GetProperty("result").EnumerateArray().ToArray());
        Assert.Equal("fn main(): int {\n    return 0;\n}\n", edit.GetProperty("newText").GetString());

        var range = edit.GetProperty("range");
        Assert.Equal(0, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(0, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(0, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(29, range.GetProperty("end").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task The_edit_covers_a_multi_line_buffer_to_its_last_character()
    {
        var uri = Uri("fmt-multiline.lyr");
        await using var harness = await OpenAsync(uri, "fn main(): int {\n  return 0;\n}");

        var id = await RequestFormattingAsync(harness, uri);
        var response = await harness.ReceiveResponseAsync(id);

        var edit = Assert.Single(response.GetProperty("result").EnumerateArray().ToArray());
        var end = edit.GetProperty("range").GetProperty("end");
        Assert.Equal(2, end.GetProperty("line").GetInt32());
        Assert.Equal(1, end.GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task A_buffer_already_in_shape_gets_an_empty_list()
    {
        var uri = Uri("fmt-clean.lyr");
        await using var harness = await OpenAsync(uri, "fn main(): int {\n    return 0;\n}\n");

        var id = await RequestFormattingAsync(harness, uri);
        var response = await harness.ReceiveResponseAsync(id);

        Assert.Equal(JsonValueKind.Array, response.GetProperty("result").ValueKind);
        Assert.Empty(response.GetProperty("result").EnumerateArray());
    }

    [Fact]
    public async Task A_buffer_that_does_not_parse_gets_no_edits()
    {
        var uri = Uri("fmt-broken.lyr");
        await using var harness = await OpenAsync(uri, "fn main( {");

        var id = await RequestFormattingAsync(harness, uri);
        var response = await harness.ReceiveResponseAsync(id);

        // Null, not an error and not an empty list: the diagnostics already on screen say why
        // nothing happened, and another provider stays free to try.
        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Comments_survive_the_editor_path_too()
    {
        var uri = Uri("fmt-comments.lyr");
        await using var harness = await OpenAsync(uri,
            "// keep me\nfn main(): int {\n    return 0; // and me\n}\n");

        var id = await RequestFormattingAsync(harness, uri);
        var response = await harness.ReceiveResponseAsync(id);

        Assert.Equal(JsonValueKind.Array, response.GetProperty("result").ValueKind);
        Assert.Empty(response.GetProperty("result").EnumerateArray());
    }
}
