using System.Text.Json;
using Lyric.Compiler;
using Lyric.Lsp.Analysis;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// Where a name was declared.
///
/// <para>The cursor is marked with <c>$</c> in the program, and the expected target with <c>^</c>,
/// so both are visible in the fixture. A test that named line and column numbers would stop being
/// about its question the moment the fixture gained a line.</para>
/// </summary>
public sealed class DefinitionTests
{
    /// <summary>
    /// Compiles a program in which <c>$</c> marks the cursor and <c>^</c> the expected target, and
    /// asserts the jump lands on the second marker.
    /// </summary>
    private static void JumpsToMarker(string marked)
    {
        var (program, cursor, expected) = Markers(marked);
        var path = Path.Combine(AppContext.BaseDirectory, "definition.lyr");

        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, program));
        Assert.NotNull(result.Model);

        var file = DiagnosticMapper.FindFile(result.Sources, path);
        var target = DefinitionProvider.At(result.Model, file, cursor);

        Assert.NotNull(target);
        Assert.Equal(file, target.File);
        Assert.Equal(expected, target.Span.Start);
    }

    private static DefinitionTarget? TargetAt(string marked, out CompileResult result)
    {
        var (program, cursor, _) = Markers(marked);
        var path = Path.Combine(AppContext.BaseDirectory, "definition.lyr");

        result = SourceCompiler.Check(ScriptSource.FromBuffer(path, program));
        Assert.NotNull(result.Model);

        var file = DiagnosticMapper.FindFile(result.Sources, path);
        return DefinitionProvider.At(result.Model, file, cursor);
    }

    /// <summary>Strips the two markers and returns the offsets they stood at.</summary>
    private static (string Program, int Cursor, int Expected) Markers(string marked)
    {
        var cursor = marked.IndexOf('$');
        Assert.True(cursor >= 0, "the fixture has no '$' marking the cursor");
        var withoutCursor = marked.Remove(cursor, 1);

        var expected = withoutCursor.IndexOf('^');
        if (expected < 0) return (withoutCursor, cursor, -1);

        var program = withoutCursor.Remove(expected, 1);

        // Each marker shifts everything after it by one. Removing the earlier one first means the
        // later offset is already correct; the earlier one needs no adjustment at all.
        return (program, cursor > expected ? cursor - 1 : cursor, expected);
    }

    [Fact]
    public void A_local_jumps_to_its_binding()
    {
        JumpsToMarker("fn main(): int {\n    ^let count = 1;\n    return c$ount;\n}\n");
    }

    [Fact]
    public void A_parameter_jumps_to_the_parameter_list()
    {
        JumpsToMarker("fn twice(^n: int): int {\n    return n$ * 2;\n}\nfn main(): int { return twice(1); }\n");
    }

    [Fact]
    public void A_call_jumps_to_the_function()
    {
        JumpsToMarker("^fn twice(n: int): int { return n * 2; }\nfn main(): int {\n    return tw$ice(1);\n}\n");
    }

    [Fact]
    public void A_type_name_jumps_to_the_type()
    {
        JumpsToMarker("^struct P { x: int, }\nfn main(): int {\n    let p: P$ = P { x = 1 };\n    return p.x;\n}\n");
    }

    [Fact]
    public void A_field_jumps_to_its_declaration()
    {
        JumpsToMarker("struct P { ^x: int, }\nfn main(): int {\n    let p = P { x = 1 };\n    return p.x$;\n}\n");
    }

    [Fact]
    public void The_target_is_the_START_of_the_declaration()
    {
        // Not its whole extent. A struct with twenty members is a twenty-line span, and selecting
        // all of it on a jump is noise rather than information.
        var target = TargetAt(
            "^struct P { x: int, y: int, z: int, }\nfn main(): int {\n    let p: P$ = P { x = 1, y = 2, z = 3 };\n    return p.x;\n}\n",
            out var result);

        Assert.NotNull(target);

        var range = SpanMapper.ToStart(result.Sources, target.Span);
        Assert.Equal(range.Start, range.End);
        Assert.Equal(0, range.Start.Line);
        Assert.Equal(0, range.Start.Character);
    }

    [Fact]
    public void A_name_that_does_not_resolve_has_no_target()
    {
        // The diagnostic on the same span already says the name is unknown. Offering the enclosing
        // statement instead would send the reader somewhere they did not ask about.
        Assert.Null(TargetAt("fn main(): int {\n    return nowh$ere;\n}\n", out _));
    }

    [Fact]
    public void A_builtin_type_has_no_target()
    {
        // 'int' is declared in no file. Answering with the enclosing binding would be worse than
        // answering with nothing.
        Assert.Null(TargetAt("fn main(): int {\n    let x: in$t = 1;\n    return x;\n}\n", out _));
    }

    [Fact]
    public void A_keyword_has_no_target()
    {
        Assert.Null(TargetAt("fn main(): int {\n    let x = 1;\n    ret$urn x;\n}\n", out _));
    }

    [Fact]
    public void A_standard_library_call_jumps_into_the_standard_library()
    {
        // The ordinary case, not an edge one. Those files are read from disk with their real paths,
        // so the span's file id names something an editor can open — which is the whole reason this
        // works without a second mechanism for "foreign" targets.
        var path = Path.Combine(AppContext.BaseDirectory, "stdlib-jump.lyr");
        const string program =
            "import std.io.console { println };\n\nfn main(): int {\n    println(\"hi\");\n    return 0;\n}\n";

        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, program));
        Assert.NotNull(result.Model);

        var file = DiagnosticMapper.FindFile(result.Sources, path);
        var target = DefinitionProvider.At(result.Model, file, program.IndexOf("println(\"hi\")") + 2);

        Assert.NotNull(target);
        Assert.NotEqual(file, target.File);

        var targetPath = result.Sources.GetPath(target.File);
        Assert.EndsWith("console.lyr", targetPath.Replace('\\', '/'));

        // A real path, so a URI can be built from it and the editor can open the file.
        Assert.True(File.Exists(targetPath));
    }
}

/// <summary>Go-to-definition across the wire.</summary>
public sealed class DefinitionProtocolTests
{
    private const string Program = "fn main(): int {\n    let count = 1;\n    return count;\n}\n";

    private static string BufferPath([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        Path.Combine(AppContext.BaseDirectory, $"{name}.lyr");

    private static string DidOpen(string uri, string text) =>
        JsonSerializer.Serialize(new
        {
            textDocument = new { uri, languageId = "lyric", version = 1, text },
        });

    private static string PositionAt(string uri, int line, int character) =>
        JsonSerializer.Serialize(new
        {
            textDocument = new { uri },
            position = new { line, character },
        });

    [Fact]
    public async Task The_capability_is_announced()
    {
        await using var harness = new ServerHarness();

        var id = await harness.RequestAsync(LspMethods.Initialize, "{}");
        var response = await harness.ReceiveResponseAsync(id);

        Assert.True(response.GetProperty("result").GetProperty("capabilities")
            .GetProperty("definitionProvider").GetBoolean());
    }

    [Fact]
    public async Task A_definition_in_the_same_file_keeps_the_uri_the_client_sent()
    {
        // Echoed rather than rebuilt. The client asked about this spelling, and a rebuilt one is a
        // different string for the same file — see DocumentUri on why those do not compare equal.
        const string asSent = "file:///c%3A/nowhere/same-file.lyr";
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(asSent, Program));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        var id = await harness.RequestAsync(LspMethods.Definition, PositionAt(asSent, 2, 12));
        var response = await harness.ReceiveResponseAsync(id);

        var result = response.GetProperty("result");
        Assert.Equal(asSent, result.GetProperty("uri").GetString());
        Assert.Equal(1, result.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(4, result.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task A_definition_in_another_file_gets_a_uri_built_from_its_path()
    {
        var uri = DocumentUri.FromFilePath(BufferPath());
        const string program =
            "import std.io.console { println };\n\nfn main(): int {\n    println(\"hi\");\n    return 0;\n}\n";

        await using var harness = new ServerHarness();
        await harness.InitializeAsync();
        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, program));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        // Line 3, on 'println'.
        var id = await harness.RequestAsync(LspMethods.Definition, PositionAt(uri, 3, 6));
        var response = await harness.ReceiveResponseAsync(id);

        var target = response.GetProperty("result").GetProperty("uri").GetString();

        Assert.NotNull(target);
        Assert.NotEqual(uri, target);
        Assert.StartsWith("file:///", target);
        Assert.EndsWith("console.lyr", target);

        // The point of building it: the client has to be able to turn it back into a file.
        Assert.True(DocumentUri.TryToFilePath(target, out var back) && File.Exists(back));
    }

    [Fact]
    public async Task A_position_with_no_definition_answers_null_rather_than_an_error()
    {
        var uri = DocumentUri.FromFilePath(BufferPath());
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, Program));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        var id = await harness.RequestAsync(LspMethods.Definition, PositionAt(uri, 0, 200));
        var response = await harness.ReceiveResponseAsync(id);

        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").ValueKind);
        Assert.False(response.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task A_definition_in_a_file_that_was_never_opened_answers_null()
    {
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        var id = await harness.RequestAsync(
            LspMethods.Definition, PositionAt(DocumentUri.FromFilePath(BufferPath()), 0, 0));
        var response = await harness.ReceiveResponseAsync(id);

        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").ValueKind);
    }
}
