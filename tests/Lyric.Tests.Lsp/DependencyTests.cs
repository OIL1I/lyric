using System.Text.Json;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// A program is more than the buffer the cursor is in.
///
/// <para>Two halves, and they are separated on purpose. The OVERLAY decides what a dependency reads
/// like — the unsaved buffer rather than the last save. The CASCADE decides when a file is looked at
/// again — when something it imports changed. Either without the other is half a feature: an overlay
/// nobody re-reads shows nothing, and a cascade over stale text refreshes to the same answer.</para>
/// </summary>
public sealed class DependencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "lyric-lsp-deps-" + Guid.NewGuid().ToString("N")[..8]);

    public DependencyTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));

        File.WriteAllText(Path.Combine(_dir, "lyric.json"), """{ "sourceRoot": "src" }""");

        // On DISK the module has what the program imports. Every test below changes that in a
        // BUFFER, so a wrong answer is one that read the file.
        File.WriteAllText(Path.Combine(_dir, "src", "util.lyr"), """
            module util;

            pub fn value(): int { return 1; }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string PathOf(string name) => Path.Combine(_dir, "src", name);

    private const string Program = """
        import util { value };

        fn main(): int { return value(); }
        """;

    /// <summary>The module without the function the program imports.</summary>
    private const string UtilWithoutValue = """
        module util;

        pub fn other(): int { return 2; }
        """;

    private static string DidOpen(string uri, string text) =>
        JsonSerializer.Serialize(new
        {
            textDocument = new { uri, languageId = "lyric", version = 1, text },
        });

    private static string DidChange(string uri, int version, string text) =>
        JsonSerializer.Serialize(new
        {
            textDocument = new { uri, version },
            contentChanges = new[] { new { text } },
        });

    /// <summary>Reads published diagnostics until a batch arrives for this URI.</summary>
    private static async Task<JsonElement> DiagnosticsFor(ServerHarness harness, string uri)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);
            if (published.GetProperty("params").GetProperty("uri").GetString() == uri)
                return published.GetProperty("params").GetProperty("diagnostics");
        }

        throw new InvalidOperationException($"no diagnostics arrived for {uri}");
    }

    [Fact]
    public async Task A_dependency_is_read_from_an_open_buffer_and_not_from_disk()
    {
        // The overlay alone: the module is opened with text that differs from the file, and the
        // program that imports it is opened AFTERWARDS. Its very first compile has to see the
        // buffer — no cascade is involved, because nothing changed after it was analysed.
        var util = DocumentUri.FromFilePath(PathOf("util.lyr"));
        var app = DocumentUri.FromFilePath(PathOf("app.lyr"));

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(util, UtilWithoutValue));
        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(app, Program));

        Assert.NotEmpty((await DiagnosticsFor(harness, app)).EnumerateArray());
    }

    [Fact]
    public async Task Editing_a_module_refreshes_the_file_that_imports_it()
    {
        // The cascade. The program is analysed while everything is fine, is never touched again,
        // and has to hear about an edit somewhere else.
        var util = DocumentUri.FromFilePath(PathOf("util.lyr"));
        var app = DocumentUri.FromFilePath(PathOf("app.lyr"));

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(app, Program));
        Assert.Empty((await DiagnosticsFor(harness, app)).EnumerateArray());

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(util, File.ReadAllText(PathOf("util.lyr"))));
        await harness.NotifyAsync(LspMethods.DidChange, DidChange(util, 2, UtilWithoutValue));

        Assert.NotEmpty((await DiagnosticsFor(harness, app)).EnumerateArray());
    }

    [Fact]
    public async Task Putting_the_function_back_clears_the_program_again()
    {
        // The other direction, which is the half a cascade usually forgets: a stale error that
        // never goes away is worse than one that arrives late.
        var util = DocumentUri.FromFilePath(PathOf("util.lyr"));
        var app = DocumentUri.FromFilePath(PathOf("app.lyr"));

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(app, Program));

        // Consumed before going on: it is the clean batch from the open, and reading it here is
        // what makes the next one the answer to the edit rather than to the open.
        Assert.Empty((await DiagnosticsFor(harness, app)).EnumerateArray());

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(util, UtilWithoutValue));
        Assert.NotEmpty((await DiagnosticsFor(harness, app)).EnumerateArray());

        await harness.NotifyAsync(LspMethods.DidChange, DidChange(util, 2, """
            module util;

            pub fn value(): int { return 41; }
            """));

        Assert.Empty((await DiagnosticsFor(harness, app)).EnumerateArray());
    }

    [Fact]
    public async Task A_module_nobody_imports_starts_no_cascade()
    {
        // The counter-check. Without it the cascade could be re-analysing every open document on
        // every keystroke and all three tests above would still pass.
        var stranger = DocumentUri.FromFilePath(PathOf("stranger.lyr"));
        var app = DocumentUri.FromFilePath(PathOf("app.lyr"));

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(app, Program));
        Assert.Empty((await DiagnosticsFor(harness, app)).EnumerateArray());

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(stranger, """
            module stranger;

            pub fn alone(): int { return 0; }
            """));

        // The next batch is the stranger's own. If the program were re-analysed for no reason, its
        // batch would arrive first.
        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);
        Assert.Equal(stranger, published.GetProperty("params").GetProperty("uri").GetString());
    }
}
