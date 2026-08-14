using System.Text.Json;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Lsp;
using Lyric.Lsp.Analysis;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// What the compiler says about the thing under the cursor.
///
/// <para>The cursor is written into the program as a marker rather than given as a line and a
/// column. A test that says "line 2, character 8" stops being about the question the moment the
/// fixture gains a line, and the reader cannot tell what it was pointing at.</para>
/// </summary>
public sealed class HoverTests
{
    /// <summary>Compiles a program in which <c>$</c> marks the cursor, and hovers there.</summary>
    private static HoverResult? HoverAt(string programWithMarker)
    {
        var offset = programWithMarker.IndexOf('$');
        Assert.True(offset >= 0, "the fixture has no '$' marking the cursor");

        var program = programWithMarker.Remove(offset, 1);
        var path = Path.Combine(AppContext.BaseDirectory, "hover.lyr");

        var result = SourceCompiler.Check(ScriptSource.FromBuffer(path, program));
        var model = result.Model;
        Assert.NotNull(model);

        var file = DiagnosticMapper.FindFile(result.Sources, path);
        Assert.True(file.IsValid);

        return HoverProvider.At(model, file, offset);
    }

    private static string Text(string programWithMarker)
    {
        var found = HoverAt(programWithMarker);
        Assert.NotNull(found);
        return found.Markdown;
    }

    [Fact]
    public void A_local_shows_its_binding_form_and_type()
    {
        Assert.Contains("let count: int", Text("fn main(): int {\n    let count = 1;\n    return c$ount;\n}\n"));
    }

    [Fact]
    public void A_mutable_local_shows_var_rather_than_let()
    {
        // The distinction is in the symbol, not in the type, and it is the thing a reader is most
        // likely to be checking.
        Assert.Contains("var count: int", Text("fn main(): int {\n    var count = 1;\n    return c$ount;\n}\n"));
    }

    [Fact]
    public void A_local_answers_at_its_declaration_too()
    {
        // The name in 'let count = 1' is no node of its own, so this only works because the search
        // walks outwards until something knows an answer.
        Assert.Contains("let count: int", Text("fn main(): int {\n    let c$ount = 1;\n    return count;\n}\n"));
    }

    [Fact]
    public void A_parameter_shows_the_type_it_was_declared_with()
    {
        Assert.Contains("n: int", Text("fn twice(n: int): int {\n    return n$ * 2;\n}\nfn main(): int { return twice(1); }\n"));
    }

    [Fact]
    public void A_function_shows_its_signature()
    {
        var text = Text("fn twice(n: int): int { return n * 2; }\nfn main(): int {\n    return tw$ice(1);\n}\n");

        Assert.Contains("fn twice(int) -> int", text);
    }

    [Fact]
    public void A_type_name_shows_what_kind_of_type_it_is()
    {
        var text = Text("struct P { x: int, }\nfn main(): int {\n    let p: P$ = P { x = 1 };\n    return p.x;\n}\n");

        Assert.Contains("struct P", text);
    }

    [Fact]
    public void A_field_access_shows_the_field_type()
    {
        var text = Text("struct P { x: int, }\nfn main(): int {\n    let p = P { x = 1 };\n    return p.x$;\n}\n");

        Assert.Contains("int", text);
    }

    [Fact]
    public void A_subexpression_without_a_symbol_still_shows_its_type()
    {
        // An operator binds to no symbol. Its type is the interesting part, and it is the question
        // hover is asked most often.
        var text = Text("fn main(): int {\n    let a = 1;\n    return a $+ 2;\n}\n");

        Assert.Contains("int", text);
    }

    [Fact]
    public void The_answer_is_wrapped_as_lyric_code()
    {
        // A fenced block with the language, so the client highlights it with the same grammar it
        // uses for the file rather than showing it as prose.
        Assert.StartsWith("```lyric\n", Text("fn main(): int {\n    let x = 1;\n    return x$;\n}\n"));
    }

    [Fact]
    public void The_range_covers_what_was_asked_about()
    {
        var found = HoverAt("fn main(): int {\n    let count = 1;\n    return c$ount;\n}\n");

        Assert.NotNull(found);
        var program = "fn main(): int {\n    let count = 1;\n    return count;\n}\n";
        Assert.Equal("count", program.Substring(found.Span.Start, found.Span.Length));
    }

    [Fact]
    public void Nothing_is_claimed_about_a_keyword()
    {
        // 'return' is not a name and carries no type. A hover here would have to invent something.
        Assert.Null(HoverAt("fn main(): int {\n    let x = 1;\n    ret$urn x;\n}\n"));
    }

    [Fact]
    public void A_generic_call_shows_the_declared_signature_with_its_type_parameters()
    {
        // A LIMIT, written down as what it is. At this call site 'T' is 'int', and showing that
        // would be better — but the substitution the sema performed lives in a private function of
        // the type checker, and a second one here would be a second answer to what 'T' became.
        //
        // So hover shows the declaration, in full: 'T' appears both in the parameter list and in
        // the angle brackets, which makes it readable as a definition rather than as a claim about
        // this call.
        var text = Text(
            "fn id<T>(v: T): T { return v; }\nfn main(): int {\n    return i$d(1);\n}\n");

        Assert.Contains("fn id<T>(T) -> T", text);
    }
}

/// <summary>Hover across the wire, including the case the whole retained-model design exists for.
/// </summary>
public sealed class HoverProtocolTests
{
    private const string Program = "fn main(): int {\n    let count = 1;\n    return count;\n}\n";

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

    private static string HoverAt(string uri, int line, int character) =>
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
            .GetProperty("hoverProvider").GetBoolean());
    }

    [Fact]
    public async Task A_hover_answers_with_the_declaration_of_the_name_under_the_cursor()
    {
        var uri = DocumentUri.FromFilePath(BufferPath());
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, 1, Program));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        // Line 2 (0-based), on 'count' in 'return count;'.
        var id = await harness.RequestAsync(LspMethods.Hover, HoverAt(uri, 2, 12));
        var response = await harness.ReceiveResponseAsync(id);

        var contents = response.GetProperty("result").GetProperty("contents");
        Assert.Equal("markdown", contents.GetProperty("kind").GetString());
        Assert.Contains("let count: int", contents.GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_hover_on_nothing_answers_null_rather_than_an_error()
    {
        // Whitespace, comments and keywords are the common case. An error here would put a failure
        // in the client's log every time the cursor rests.
        var uri = DocumentUri.FromFilePath(BufferPath());
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, 1, Program));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        var id = await harness.RequestAsync(LspMethods.Hover, HoverAt(uri, 0, 200));
        var response = await harness.ReceiveResponseAsync(id);

        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").ValueKind);
        Assert.False(response.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task A_hover_on_a_file_that_was_never_opened_answers_null()
    {
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        var id = await harness.RequestAsync(
            LspMethods.Hover, HoverAt(DocumentUri.FromFilePath(BufferPath()), 0, 0));
        var response = await harness.ReceiveResponseAsync(id);

        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task A_hover_still_answers_while_the_buffer_does_not_parse()
    {
        // The reason the last good analysis is kept at all. Mid-edit is the normal state of a file
        // someone is looking things up in, and a server that went silent then would be silent
        // exactly when it is needed.
        var uri = DocumentUri.FromFilePath(BufferPath());
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, 1, Program));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        // A trailing '.' with nothing after it: what a buffer looks like halfway through a member
        // access.
        await harness.NotifyAsync(LspMethods.DidChange,
            DidChange(uri, 2, Program.Replace("return count;", "return count.;")));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        var id = await harness.RequestAsync(LspMethods.Hover, HoverAt(uri, 2, 12));
        var response = await harness.ReceiveResponseAsync(id);

        Assert.Contains("count", response.GetProperty("result")
            .GetProperty("contents").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Closing_a_document_forgets_what_was_known_about_it()
    {
        var uri = DocumentUri.FromFilePath(BufferPath());
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.DidOpen, DidOpen(uri, 1, Program));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);
        await harness.NotifyAsync(LspMethods.DidClose,
            JsonSerializer.Serialize(new { textDocument = new { uri } }));
        await harness.ReceiveNotificationAsync(LspMethods.PublishDiagnostics);

        var id = await harness.RequestAsync(LspMethods.Hover, HoverAt(uri, 2, 12));
        var response = await harness.ReceiveResponseAsync(id);

        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").ValueKind);
    }
}
