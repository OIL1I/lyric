using System.Text.Json;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>The inlay hints over the wire — the path the provider tests skipped.</summary>
public sealed class InlayWireTests
{
    [Fact]
    public async Task The_hint_arrives_over_the_wire()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "inlay-wire.lyr");
        var uri = DocumentUri.FromFilePath(path);
        const string text = "fn main(): int {\n    let total = 1 + 2;\n    return total;\n}\n";

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();
        await harness.NotifyAsync(LspMethods.DidOpen, JsonSerializer.Serialize(new
        {
            textDocument = new { uri, languageId = "lyric", version = 1, text },
        }));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        var id = await harness.RequestAsync(LspMethods.InlayHint, JsonSerializer.Serialize(new
        {
            textDocument = new { uri },
            range = new
            {
                start = new { line = 0, character = 0 },
                end = new { line = 10, character = 0 },
            },
        }));

        var response = await harness.ReceiveResponseAsync(id);
        Assert.False(response.TryGetProperty("error", out var error),
            response.TryGetProperty("error", out error) ? error.ToString() : "");

        var hints = response.GetProperty("result").EnumerateArray().ToArray();
        var hint = Assert.Single(hints);

        Assert.Equal(": int", hint.GetProperty("label").GetString());
        Assert.Equal(1, hint.GetProperty("kind").GetInt32());
        Assert.Equal(1, hint.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(13, hint.GetProperty("position").GetProperty("character").GetInt32());
    }
}
