using System.Text.Json;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// The reason <c>lyric.json</c> is declarative: an editor has to learn a project's layout without
/// running anything from it.
///
/// <para>These use a REAL DIRECTORY rather than the buffer-path trick the other tests use. The
/// project file is found by walking up from the document, so where the document claims to be is the
/// whole question.</para>
/// </summary>
public sealed class ProjectFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-lsp-project-" + Guid.NewGuid().ToString("N")[..8]);

    public ProjectFileTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "sdk", "engine"));

        File.WriteAllText(Path.Combine(_dir, "sdk", "engine", "input.lyr"), """
            module engine.input;

            pub fn keyDown(key: int): bool;
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void WriteProject(string text) => File.WriteAllText(Path.Combine(_dir, "lyric.json"), text);

    private string EntryPath => Path.Combine(_dir, "src", "main.lyr");

    private const string ImportsTheSdk = """
        import engine.input { keyDown };

        fn main(): int { return if (keyDown(32)) 1 else 0; }
        """;

    private static string DidOpen(string uri, string text) =>
        JsonSerializer.Serialize(new
        {
            textDocument = new { uri, languageId = "lyric", version = 1, text },
        });

    private static JsonElement Diagnostics(JsonElement notification) =>
        notification.GetProperty("params").GetProperty("diagnostics");

    [Fact]
    public async Task A_native_root_from_the_project_file_resolves()
    {
        // The gap v1.1.0 shipped with: the editor said "cannot find module" for an import the host
        // resolved at runtime.
        WriteProject("""{ "sourceRoot": "src", "nativeRoots": { "engine": "sdk" } }""");

        var uri = DocumentUri.FromFilePath(EntryPath);
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, ImportsTheSdk));
        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        Assert.Empty(Diagnostics(published).EnumerateArray());
    }

    [Fact]
    public async Task Without_the_project_file_the_import_is_unknown()
    {
        // The counter-check. Without it the test above would pass on a server that resolves the
        // import for some other reason, and the file would be proving nothing.
        var uri = DocumentUri.FromFilePath(EntryPath);
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, ImportsTheSdk));
        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        Assert.NotEmpty(Diagnostics(published).EnumerateArray());
    }

    [Fact]
    public async Task A_broken_project_file_still_leaves_the_editor_with_diagnostics()
    {
        // The one that matters for a tool someone is looking at. Falling back to the plain rules
        // gives resolution that may be wrong; publishing nothing would leave the editor showing an
        // earlier state with no hint that anything happened.
        WriteProject("""{ "sourceRoot": """);

        var uri = DocumentUri.FromFilePath(EntryPath);
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, ImportsTheSdk));

        // The import is unresolved, because the file that would have granted the root is unusable —
        // but an answer arrives, which is the point.
        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);
        Assert.NotEmpty(Diagnostics(published).EnumerateArray());
    }

    [Fact]
    public async Task The_source_root_decides_where_a_neighbour_is_looked_for()
    {
        // 'shapes.area' lies under src/, and the document lies under src/ too — so this passes
        // without a project file as well. What it pins is that declaring a sourceRoot does not
        // BREAK the ordinary case, which is the whole additive promise.
        WriteProject("""{ "sourceRoot": "src" }""");
        File.WriteAllText(Path.Combine(_dir, "src", "area.lyr"), """
            module area;

            pub fn square(n: int): int { return n * n; }
            """);

        var uri = DocumentUri.FromFilePath(EntryPath);
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, """
            import area { square };

            fn main(): int { return square(7); }
            """));
        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        Assert.Empty(Diagnostics(published).EnumerateArray());
    }
}
