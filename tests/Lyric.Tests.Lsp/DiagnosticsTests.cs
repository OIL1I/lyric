using System.Text.Json;
using Lyric.Compiler;
using Lyric.Lsp;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// What the whole thing is for: an editor gets the compiler's answer about the buffer it holds.
///
/// <para>The programs are chosen from a measured run rather than guessed. <c>Faulty</c> produces
/// exactly one diagnostic, on the SECOND line, which is what makes the 1-based to 0-based
/// conversion visible — an error on the first line reads the same either way.</para>
/// </summary>
public sealed class DiagnosticsTests
{
    private const string Clean = "fn main(): int {\n    return 0;\n}\n";

    private const string Faulty = "fn main(): int {\n    let x: int = \"not an int\";\n    return 0;\n}\n";

    /// <summary>A path in the output directory. It need not exist: the buffer carries the text and
    /// the path is only the identity.</summary>
    private static string BufferPath([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        Path.Combine(AppContext.BaseDirectory, $"{name}.lyr");

    private static string DidOpen(string uri, int version, string text) =>
        JsonSerializer.Serialize(new
        {
            textDocument = new { uri, languageId = "lyric", version, text },
        });

    private static string DidChange(string uri, int version, string text) =>
        JsonSerializer.Serialize(new
        {
            textDocument = new { uri, version },
            contentChanges = new[] { new { text } },
        });

    private static string DidClose(string uri) =>
        JsonSerializer.Serialize(new { textDocument = new { uri } });

    private static JsonElement Diagnostics(JsonElement notification) =>
        notification.GetProperty("params").GetProperty("diagnostics");

    [Fact]
    public async Task A_program_that_compiles_publishes_an_empty_list()
    {
        // An empty list rather than no message. The client has to be told that what it was shown
        // before is gone.
        var uri = DocumentUri.FromFilePath(BufferPath());
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, 1, Clean));
        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        Assert.Equal(uri, published.GetProperty("params").GetProperty("uri").GetString());
        Assert.Empty(Diagnostics(published).EnumerateArray());
    }

    [Fact]
    public async Task An_error_arrives_at_the_position_the_compiler_reports_counted_from_zero()
    {
        var uri = DocumentUri.FromFilePath(BufferPath());
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, 1, Faulty));
        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        var diagnostic = Assert.Single(Diagnostics(published).EnumerateArray().ToList());

        Assert.Equal("LYR-SEM0001", diagnostic.GetProperty("code").GetString());
        Assert.Equal((int)LspSeverity.Error, diagnostic.GetProperty("severity").GetInt32());
        Assert.Equal("lyric", diagnostic.GetProperty("source").GetString());

        // The compiler renders this at line 2, column 5. Both origins move by one, and nothing
        // else does: the character is a UTF-16 offset on both sides.
        var start = diagnostic.GetProperty("range").GetProperty("start");
        Assert.Equal(1, start.GetProperty("line").GetInt32());
        Assert.Equal(4, start.GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task The_server_reports_what_the_compiler_reports_for_the_same_text()
    {
        // The invariant worth pinning: not a hand-written expectation but agreement with the
        // pipeline 'lyrc check' runs. It keeps holding as diagnostics are added or reworded.
        var path = BufferPath();
        var uri = DocumentUri.FromFilePath(path);

        var direct = SourceCompiler.Check(ScriptSource.FromBuffer(path, Faulty))
            .Diagnostics.SortedSnapshot()
            .Where(d => d.Span.File.IsValid)
            .Select(d => d.Code)
            .ToArray();

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();
        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, 1, Faulty));
        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        var served = Diagnostics(published).EnumerateArray()
            .Select(d => d.GetProperty("code").GetString())
            .ToArray();

        Assert.Equal(direct, served);
    }

    [Fact]
    public async Task Nothing_from_the_standard_library_reaches_the_user_document()
    {
        // Every compile parses and checks the standard library too. Its file ids are in the same
        // source manager, and an unfiltered publish would decorate the user's buffer with
        // positions from a file they never opened.
        var uri = DocumentUri.FromFilePath(BufferPath());
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, 1, Clean));
        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        Assert.Empty(Diagnostics(published).EnumerateArray());
    }

    [Fact]
    public async Task Fixing_the_buffer_withdraws_the_diagnostic()
    {
        var uri = DocumentUri.FromFilePath(BufferPath());
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, 1, Faulty));
        Assert.NotEmpty(Diagnostics(
            await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics)).EnumerateArray());

        await harness.NotifyAsync(LspMethods.DidChange, DidChange(uri, 2, Clean));
        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        Assert.Empty(Diagnostics(published).EnumerateArray());
        Assert.Equal(2, published.GetProperty("params").GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Closing_a_document_withdraws_its_diagnostics()
    {
        // Without this the squiggles of a closed file stay in the editor for the rest of the
        // session: diagnostics are state the server owns in the client, and a server that just
        // stops talking about a file never releases it.
        var uri = DocumentUri.FromFilePath(BufferPath());
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, 1, Faulty));
        Assert.NotEmpty(Diagnostics(
            await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics)).EnumerateArray());

        await harness.NotifyAsync(LspMethods.DidClose, DidClose(uri));
        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        Assert.Equal(uri, published.GetProperty("params").GetProperty("uri").GetString());
        Assert.Empty(Diagnostics(published).EnumerateArray());
    }

    [Fact]
    public async Task A_superseded_version_is_never_published()
    {
        // The debounce is long relative to the two sends, so the middle version is cancelled while
        // it is still waiting. Without the guard the first notification after the edits carries
        // version 2, and its offsets describe text the user has already replaced.
        var uri = DocumentUri.FromFilePath(BufferPath());
        await using var harness = new ServerHarness(new LspServerOptions
        {
            Debounce = TimeSpan.FromMilliseconds(400),
        });
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, 1, Clean));
        Assert.Equal(1, (await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics))
            .GetProperty("params").GetProperty("version").GetInt32());

        await harness.NotifyAsync(LspMethods.DidChange, DidChange(uri, 2, Faulty));
        await harness.NotifyAsync(LspMethods.DidChange, DidChange(uri, 3, Clean));

        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        Assert.Equal(3, published.GetProperty("params").GetProperty("version").GetInt32());
        Assert.Empty(Diagnostics(published).EnumerateArray());
    }

    [Fact]
    public async Task A_buffer_with_no_path_is_ignored_rather_than_analysed()
    {
        // 'untitled:' is what an editor sends for a file that was never saved. It cannot be
        // compiled, and inventing a path for it would resolve its imports against a directory
        // nobody chose.
        var uri = DocumentUri.FromFilePath(BufferPath());
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen("untitled:Untitled-1", 1, Faulty));
        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, 1, Clean));

        // The proof it was skipped: the FIRST publication is about the file, not about the
        // untitled buffer that was opened before it.
        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        Assert.Equal(uri, published.GetProperty("params").GetProperty("uri").GetString());
    }
}
