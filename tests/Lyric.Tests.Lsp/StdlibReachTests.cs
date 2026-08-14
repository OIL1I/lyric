using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// That the server compiles against the real standard library.
///
/// <para>Without this the rest of this suite could be green for the wrong reason. A missing
/// standard library does not fail loudly — an unresolved import becomes an opaque external symbol,
/// so a program that uses none of it still checks clean, and every test built on such a program
/// would agree with a server that can do nothing.</para>
/// </summary>
public sealed class StdlibReachTests
{
    private static string BufferPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, $"{name}.lyr");

    private static string DidOpen(string uri, string text) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            textDocument = new { uri, languageId = "lyric", version = 1, text },
        });

    [Fact]
    public async Task A_program_using_the_standard_library_checks_clean()
    {
        const string program =
            "import std.io.console { println };\n\nfn main(): int {\n    println(\"hi\");\n    return 0;\n}\n";

        var uri = DocumentUri.FromFilePath(BufferPath("stdlib-user"));
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, program));
        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        Assert.Empty(published.GetProperty("params").GetProperty("diagnostics").EnumerateArray());
    }

    [Fact]
    public async Task A_wrong_call_into_the_standard_library_is_caught()
    {
        // The counter-check, and the half that actually proves the library was READ rather than
        // merely tolerated: an opaque external symbol accepts any argument list, so only a
        // rejected call shows that the real signature was in hand.
        const string program =
            "import std.io.console { println };\n\nfn main(): int {\n    println(1, 2, 3);\n    return 0;\n}\n";

        var uri = DocumentUri.FromFilePath(BufferPath("stdlib-misuse"));
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, program));
        var published = await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        Assert.NotEmpty(published.GetProperty("params").GetProperty("diagnostics").EnumerateArray());
    }
}
