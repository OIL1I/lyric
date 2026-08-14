using System.Text.Json;
using Lyric.Core;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// The state machine around a session.
///
/// <para>Clients check this. A server that answers before <c>initialize</c> or after
/// <c>shutdown</c> is not merely lax — it hands out answers about a state it has not got, and the
/// exit code is the one part of the whole protocol a process supervisor can see.</para>
/// </summary>
public sealed class LifecycleTests
{
    private static int ErrorCode(JsonElement response) =>
        response.GetProperty("error").GetProperty("code").GetInt32();

    [Fact]
    public async Task Announces_full_synchronisation_and_the_encoding_spans_already_use()
    {
        await using var harness = new ServerHarness();

        var id = await harness.RequestAsync(LspMethods.Initialize, "{}");
        var response = await harness.ReceiveResponseAsync(id);

        var capabilities = response.GetProperty("result").GetProperty("capabilities");
        Assert.Equal((int)TextDocumentSyncKind.Full,
            capabilities.GetProperty("textDocumentSync").GetProperty("change").GetInt32());

        // Announcing anything else would mean converting every offset: Span counts UTF-16 code
        // units, and so does this.
        Assert.Equal("utf-16", capabilities.GetProperty("positionEncoding").GetString());

        // Without it the client is entitled to never send didClose, and stale diagnostics would
        // stay in the editor for the rest of the session.
        Assert.True(capabilities.GetProperty("textDocumentSync").GetProperty("openClose").GetBoolean());

        Assert.Equal(ToolchainVersion.Value,
            response.GetProperty("result").GetProperty("serverInfo").GetProperty("version").GetString());
    }

    [Fact]
    public async Task A_request_before_initialize_is_refused_with_the_code_that_says_to_wait()
    {
        await using var harness = new ServerHarness();

        var id = await harness.RequestAsync(LspMethods.Shutdown);
        var response = await harness.ReceiveResponseAsync(id);

        Assert.Equal(JsonRpcErrorCodes.ServerNotInitialized, ErrorCode(response));
    }

    [Fact]
    public async Task A_second_initialize_is_refused()
    {
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        var id = await harness.RequestAsync(LspMethods.Initialize, "{}");
        var response = await harness.ReceiveResponseAsync(id);

        Assert.Equal(JsonRpcErrorCodes.InvalidRequest, ErrorCode(response));
    }

    [Fact]
    public async Task Shutdown_answers_with_a_result_that_is_present_and_null()
    {
        // Not the same as a response without a result. Some clients wait on the member being there.
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        var id = await harness.RequestAsync(LspMethods.Shutdown);
        var response = await harness.ReceiveResponseAsync(id);

        Assert.True(response.TryGetProperty("result", out var result));
        Assert.Equal(JsonValueKind.Null, result.ValueKind);
        Assert.False(response.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task A_request_after_shutdown_is_refused()
    {
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();
        var shutdown = await harness.RequestAsync(LspMethods.Shutdown);
        await harness.ReceiveResponseAsync(shutdown);

        var id = await harness.RequestAsync(LspMethods.Shutdown);
        var response = await harness.ReceiveResponseAsync(id);

        Assert.Equal(JsonRpcErrorCodes.InvalidRequest, ErrorCode(response));
    }

    [Fact]
    public async Task An_unknown_request_is_method_not_found()
    {
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        var id = await harness.RequestAsync("textDocument/hover", "{}");
        var response = await harness.ReceiveResponseAsync(id);

        // The honest answer for a capability this server does not announce. Answering null instead
        // would claim the feature exists and found nothing.
        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, ErrorCode(response));
    }

    [Fact]
    public async Task An_unknown_notification_is_dropped_without_disturbing_the_session()
    {
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync("$/somethingNobodyImplements", "{}");

        // The proof it was dropped rather than answered: the next request still gets ITS id back.
        var id = await harness.RequestAsync(LspMethods.Shutdown);
        var response = await harness.ReceiveResponseAsync(id);

        Assert.True(response.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Malformed_json_costs_one_message_and_not_the_session()
    {
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        // The frame was intact, so the stream is still located. Only this message is lost.
        await harness.SendAsync(@"{""jsonrpc"":""2.0"",""id"":99,""method"":");
        var parseError = await harness.ReceiveAsync();
        Assert.Equal(JsonRpcErrorCodes.ParseError, ErrorCode(parseError));

        var id = await harness.RequestAsync(LspMethods.Shutdown);
        Assert.True((await harness.ReceiveResponseAsync(id)).TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Exit_after_shutdown_is_a_clean_end()
    {
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();
        var id = await harness.RequestAsync(LspMethods.Shutdown);
        await harness.ReceiveResponseAsync(id);

        await harness.NotifyAsync(LspMethods.Exit);

        Assert.Equal(ExitCodes.Success, await harness.ExitCodeAsync());
    }

    [Fact]
    public async Task Exit_without_shutdown_is_a_failure()
    {
        // The distinction the specification asks for, and the one that tells a closed editor from a
        // crashed one in a supervisor's log.
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.NotifyAsync(LspMethods.Exit);

        Assert.Equal(ExitCodes.Failure, await harness.ExitCodeAsync());
    }

    [Fact]
    public async Task Exit_is_obeyed_even_before_initialize()
    {
        // The one way out of a session that never started.
        await using var harness = new ServerHarness();

        await harness.NotifyAsync(LspMethods.Exit);

        Assert.Equal(ExitCodes.Failure, await harness.ExitCodeAsync());
    }

    [Fact]
    public async Task A_client_that_simply_disappears_ends_the_run_as_a_failure()
    {
        await using var harness = new ServerHarness();
        await harness.InitializeAsync();

        await harness.CloseInputAsync();

        Assert.Equal(ExitCodes.Failure, await harness.ExitCodeAsync());
    }
}
