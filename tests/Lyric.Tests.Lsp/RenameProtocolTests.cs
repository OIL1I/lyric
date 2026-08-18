using System.Text.Json;
using Lyric.Compiler;
using Lyric.Lsp.Analysis;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// The rename and the symbol search over the wire — and the proof that counts: the returned edits,
/// APPLIED, produce a project that compiles with the new name and without the old one.
/// </summary>
public sealed class RenameProtocolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-lsp-renameproto-" + Guid.NewGuid().ToString("N")[..8]);

    public RenameProtocolTests()
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

    private static string DidOpen(string uri, string text) =>
        JsonSerializer.Serialize(new
        {
            textDocument = new { uri, languageId = "lyric", version = 1, text },
        });

    private static async Task WaitForDiagnostics(ServerHarness harness, string uri)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);
            if (published.GetProperty("params").GetProperty("uri").GetString() == uri) return;
        }

        throw new InvalidOperationException($"no diagnostics arrived for {uri}");
    }

    /// <summary>
    /// Applies LSP text edits to a document, last edit first so earlier offsets stay valid.
    /// </summary>
    private static string Apply(string text, IEnumerable<JsonElement> edits)
    {
        var ordered = edits
            .Select(edit => (
                Start: TextOffsets.ToOffset(text, Read(edit.GetProperty("range")
                    .GetProperty("start"))),
                End: TextOffsets.ToOffset(text, Read(edit.GetProperty("range")
                    .GetProperty("end"))),
                New: edit.GetProperty("newText").GetString()!))
            .OrderByDescending(edit => edit.Start);

        foreach (var (start, end, replacement) in ordered)
            text = text[..start] + replacement + text[end..];

        return text;

        static Position Read(JsonElement position) => new()
        {
            Line = position.GetProperty("line").GetInt32(),
            Character = position.GetProperty("character").GetInt32(),
        };
    }

    [Fact]
    public async Task A_rename_applied_leaves_a_compiling_project_with_the_new_name()
    {
        var utilText = "module util;\n\npub fn value(): int { return 1; }\n";
        var appText = "import util { value };\n\nfn main(): int { return value(); }\n";
        var util = Write("util.lyr", utilText);
        var app = Write("app.lyr", appText);
        var utilUri = DocumentUri.FromFilePath(util);
        var appUri = DocumentUri.FromFilePath(app);

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();
        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(utilUri, utilText));
        await WaitForDiagnostics(harness, utilUri);

        // The rename is asked ON THE DECLARATION, which is where the other direction begins:
        // 'pub fn value' stands at line 2, the name from character 7.
        var id = await harness.RequestAsync(LspMethods.Rename, JsonSerializer.Serialize(new
        {
            textDocument = new { uri = utilUri },
            position = new { line = 2, character = 8 },
            newName = "amount",
        }));

        var response = await harness.ReceiveResponseAsync(id);
        var changes = response.GetProperty("result").GetProperty("changes");

        var texts = new Dictionary<string, string> { [utilUri] = utilText, [appUri] = appText };
        var touched = 0;
        foreach (var change in changes.EnumerateObject())
        {
            touched++;
            Assert.True(texts.ContainsKey(change.Name), $"unexpected edit target {change.Name}");
            texts[change.Name] = Apply(texts[change.Name], change.Value.EnumerateArray());
        }
        Assert.Equal(2, touched);

        // The applied result is the arbiter: it compiles, the new name is everywhere the old one
        // was, and the old name survives nowhere.
        File.WriteAllText(util, texts[utilUri]);
        File.WriteAllText(app, texts[appUri]);

        var recompiled = SourceCompiler.CheckProject(
            [ScriptSource.FromDisk(app, "app"), ScriptSource.FromDisk(util, "util")],
            new CompilerOptions { SourceRoot = Path.Combine(_dir, "src") });

        Assert.True(recompiled.Ok, string.Join("\n",
            recompiled.Diagnostics.SortedSnapshot().Select(d => $"{d.Code}: {d.Message}")));
        Assert.DoesNotContain("value", texts[utilUri]);
        Assert.DoesNotContain("value", texts[appUri]);
        Assert.Contains("amount", texts[appUri]);
    }

    [Fact]
    public async Task A_refused_rename_carries_its_reason_to_the_client()
    {
        var appText = "import std.io.console { println };\n\n"
            + "fn main(): int {\n    println(\"hi\");\n    return 0;\n}\n";
        var app = Write("app.lyr", appText);
        var appUri = DocumentUri.FromFilePath(app);

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();
        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(appUri, appText));
        await WaitForDiagnostics(harness, appUri);

        // 'println' inside main: line 3, character 5.
        var id = await harness.RequestAsync(LspMethods.PrepareRename, JsonSerializer.Serialize(new
        {
            textDocument = new { uri = appUri },
            position = new { line = 3, character = 6 },
        }));

        var response = await harness.ReceiveResponseAsync(id);
        var error = response.GetProperty("error");

        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Contains("outside this project", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_workspace_symbol_search_spans_the_project()
    {
        var utilText = "module util;\n\npub fn value(): int { return 1; }\n"
            + "pub struct Holder { amount: int, }\n";
        var appText = "import util { value };\n\nfn main(): int { return value(); }\n";
        var util = Write("util.lyr", utilText);
        Write("app.lyr", appText);
        var utilUri = DocumentUri.FromFilePath(util);
        var appUri = DocumentUri.FromFilePath(Path.Combine(_dir, "src", "app.lyr"));

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        // Opening APP is enough: the symbols of util come from the same project compilation,
        // which is the point of asking the workspace rather than a document.
        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(appUri, appText));
        await WaitForDiagnostics(harness, appUri);

        var id = await harness.RequestAsync(LspMethods.WorkspaceSymbol,
            """{"query":"hold"}""");

        var response = await harness.ReceiveResponseAsync(id);
        var results = response.GetProperty("result").EnumerateArray().ToArray();

        var holder = Assert.Single(results);
        Assert.Equal("Holder", holder.GetProperty("name").GetString());
        Assert.Equal(utilUri, holder.GetProperty("location").GetProperty("uri").GetString());

        // The field inside it is found too, with its container names — asked with a query that
        // matches the field.
        id = await harness.RequestAsync(LspMethods.WorkspaceSymbol, """{"query":"amount"}""");
        response = await harness.ReceiveResponseAsync(id);
        var amount = Assert.Single(response.GetProperty("result").EnumerateArray().ToArray());
        Assert.Equal("Holder", amount.GetProperty("containerName").GetString());
    }
}
