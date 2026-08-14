using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Lyric.Core;
using Lyric.Lsp.Analysis;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Lsp;

/// <summary>What a server run needs besides its two streams.</summary>
public sealed record LspServerOptions
{
    /// <summary>
    /// How long a document must be quiet before it is compiled.
    ///
    /// <para>Its purpose is to skip versions, not to delay the answer. The number is small because
    /// a whole compile in this process costs 7 to 16 ms for a file of a hundred lines, standard
    /// library included — the two hundred milliseconds a batch invocation of the compiler takes
    /// are process start and JIT warm-up, neither of which a long-lived server pays. At that price
    /// the debounce is the DOMINANT latency, so it is set to coalesce a burst of keystrokes and
    /// nothing more.</para>
    /// </summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>As in <see cref="Compiler.CompilerOptions.StdlibRoot"/>: <c>null</c> is the
    /// directory beside the binary.</summary>
    public string? StdlibRoot { get; init; }
}

/// <summary>
/// The server: one read loop, a lifecycle, and a dispatcher.
///
/// <para>The loop NEVER WAITS ON AN ANALYSIS. Everything a message triggers is either immediate or
/// handed to <see cref="AnalysisService"/>, because the message that makes the current analysis
/// pointless is the next one in the stream, and a loop parked on a compile cannot read it.</para>
///
/// <para>The lifecycle is a real state machine and not a pair of flags. A client is entitled to an
/// error rather than an answer before <c>initialize</c> and after <c>shutdown</c>, and the exit
/// code says which of the two ways the session ended — the one part of this protocol a process
/// supervisor can see.</para>
/// </summary>
public sealed class LspServer : IDisposable
{
    private enum State
    {
        /// <summary>Nothing but <c>initialize</c> is answered.</summary>
        Starting,

        Running,

        /// <summary><c>shutdown</c> has been answered; only <c>exit</c> is still expected.</summary>
        ShuttingDown,
    }

    private readonly LspConnection _connection;
    private readonly LspServerOptions _options;
    private readonly DocumentStore _documents = new();
    private readonly AnalysisService _analysis;

    private State _state = State.Starting;
    private bool _exitRequested;

    public LspServer(Stream input, Stream output, LspServerOptions? options = null)
    {
        _options = options ?? new LspServerOptions();
        _connection = new LspConnection(input, output);
        _analysis = new AnalysisService(
            _documents,
            PublishDiagnosticsAsync,
            LogAsync,
            _options.Debounce,
            _options.StdlibRoot);
    }

    /// <summary>
    /// Serves until <c>exit</c> or end of stream, and returns the process exit code.
    ///
    /// <para><see cref="ExitCodes.Success"/> only after a <c>shutdown</c> was received.
    /// An <c>exit</c> without one, or a stream that simply stops, means the client went away
    /// unexpectedly and is <see cref="ExitCodes.Failure"/> — the specification asks for exactly
    /// this distinction, and it is what tells a crashed editor from a closed one.</para>
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (!_exitRequested)
            {
                var payload = await _connection.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (payload is null) break; // the client closed the stream

                await HandlePayloadAsync(payload, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is shutting us down. Not a protocol fault.
        }
        catch (LspProtocolException exception)
        {
            // The framing is gone, so no further message can be located in the stream. Reporting
            // it over that same stream would be guesswork; the exit code carries it instead.
            await SafeLogAsync($"protocol error: {exception.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        return _state == State.ShuttingDown ? ExitCodes.Success : ExitCodes.Failure;
    }

    private async Task HandlePayloadAsync(byte[] payload, CancellationToken cancellationToken)
    {
        JsonRpcMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(payload, LspJson.Default.JsonRpcMessage);
        }
        catch (JsonException)
        {
            // The frame was intact, so the stream is still usable and the next message will be
            // read normally. Only this one is lost, and the client is told which error it was.
            await SendErrorAsync(null, JsonRpcErrorCodes.ParseError, "message is not valid JSON",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message is null || message.Method is null)
        {
            // A response to something we never sent: this server issues no requests of its own.
            await SendErrorAsync(message?.Id, JsonRpcErrorCodes.InvalidRequest,
                "expected a request or a notification", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message.IsRequest)
            await HandleRequestAsync(message, message.Id!.Value, cancellationToken).ConfigureAwait(false);
        else
            await HandleNotificationAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleRequestAsync(
        JsonRpcMessage message, JsonElement id, CancellationToken cancellationToken)
    {
        switch (message.Method)
        {
            case LspMethods.Initialize when _state == State.Starting:
                _state = State.Running;
                await SendResultAsync(id, BuildInitializeResult(),
                    LspJson.Default.InitializeResult, cancellationToken).ConfigureAwait(false);
                return;

            case LspMethods.Initialize:
                await SendErrorAsync(id, JsonRpcErrorCodes.InvalidRequest,
                    "the server is already initialized", cancellationToken).ConfigureAwait(false);
                return;

            case LspMethods.Shutdown when _state == State.Running:
                _state = State.ShuttingDown;

                // Everything pending is withdrawn here rather than at 'exit': after this answer
                // the client is entitled to assume the server produces nothing further, and a
                // diagnostic arriving afterwards would be exactly that.
                _analysis.Dispose();
                await SendResultAsync(id, cancellationToken).ConfigureAwait(false);
                return;
        }

        if (_state == State.Starting)
        {
            await SendErrorAsync(id, JsonRpcErrorCodes.ServerNotInitialized,
                $"'{message.Method}' before 'initialize'", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_state == State.ShuttingDown)
        {
            await SendErrorAsync(id, JsonRpcErrorCodes.InvalidRequest,
                $"'{message.Method}' after 'shutdown'", cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendErrorAsync(id, JsonRpcErrorCodes.MethodNotFound,
            $"'{message.Method}' is not implemented", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleNotificationAsync(
        JsonRpcMessage message, CancellationToken cancellationToken)
    {
        // 'exit' is answered in every state, including before 'initialize'. It is the one way out
        // of a session that never started.
        if (message.Method == LspMethods.Exit)
        {
            _exitRequested = true;
            return;
        }

        if (_state != State.Running) return;

        switch (message.Method)
        {
            case LspMethods.Initialized:
                // Acknowledgement only. Nothing is registered dynamically.
                return;

            case LspMethods.DidOpen:
                HandleDidOpen(message);
                return;

            case LspMethods.DidChange:
                HandleDidChange(message);
                return;

            case LspMethods.DidClose:
                await HandleDidCloseAsync(message, cancellationToken).ConfigureAwait(false);
                return;

            case LspMethods.DidSave:
                // The buffer is authoritative and was analysed when it changed. Saving adds
                // nothing to know.
                return;

            case LspMethods.CancelRequest:
                // Accepted and ignored, deliberately: this server has no long-running REQUEST to
                // cancel. Diagnostics are pushed rather than asked for, and their staleness is
                // handled by the version guard in AnalysisService instead.
                return;

            default:
                // An unknown notification is dropped. The protocol requires that of the '$/'
                // namespace and permits it everywhere else.
                return;
        }
    }

    private void HandleDidOpen(JsonRpcMessage message)
    {
        var parameters = LspJson.ReadParams(
            message.Params, LspJson.Default.DidOpenTextDocumentParams);
        if (parameters is null) return;

        var document = _documents.Set(
            parameters.TextDocument.Uri,
            parameters.TextDocument.Version,
            parameters.TextDocument.Text);

        if (document is not null) _analysis.Schedule(document);
    }

    private void HandleDidChange(JsonRpcMessage message)
    {
        var parameters = LspJson.ReadParams(
            message.Params, LspJson.Default.DidChangeTextDocumentParams);
        if (parameters is null) return;

        // Full synchronisation: the last change carries the whole document. An empty list is not
        // an error, it is a change that changed nothing.
        if (parameters.ContentChanges.Count == 0) return;

        var document = _documents.Set(
            parameters.TextDocument.Uri,
            parameters.TextDocument.Version,
            parameters.ContentChanges[^1].Text);

        if (document is not null) _analysis.Schedule(document);
    }

    private async Task HandleDidCloseAsync(
        JsonRpcMessage message, CancellationToken cancellationToken)
    {
        var parameters = LspJson.ReadParams(
            message.Params, LspJson.Default.DidCloseTextDocumentParams);
        if (parameters is null) return;

        var document = _documents.Remove(parameters.TextDocument.Uri);
        if (document is null) return;

        await _analysis.ClearAsync(document, cancellationToken).ConfigureAwait(false);
    }

    private InitializeResult BuildInitializeResult() => new()
    {
        Capabilities = new ServerCapabilities
        {
            TextDocumentSync = new TextDocumentSyncOptions { Change = TextDocumentSyncKind.Full },

            // The encoding Span already counts in. Announcing anything else would mean converting
            // every offset twice per message for no gain.
            PositionEncoding = "utf-16",
        },
        ServerInfo = new ServerInfo { Name = "lyrls", Version = ToolchainVersion.Value },
    };

    private Task PublishDiagnosticsAsync(
        PublishDiagnosticsParams parameters, CancellationToken cancellationToken) =>
        SendNotificationAsync(LspMethods.PublishDiagnostics,
            LspJson.ToElement(parameters, LspJson.Default.PublishDiagnosticsParams),
            cancellationToken);

    private Task LogAsync(string message, MessageType type, CancellationToken cancellationToken) =>
        SendNotificationAsync(LspMethods.LogMessage,
            LspJson.ToElement(new LogMessageParams { Type = type, Message = message },
                LspJson.Default.LogMessageParams),
            cancellationToken);

    private async Task SafeLogAsync(string message)
    {
        try
        {
            await LogAsync(message, MessageType.Error, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Nowhere left to report to.
        }
    }

    private Task SendNotificationAsync(
        string method, JsonElement parameters, CancellationToken cancellationToken) =>
        SendAsync(new JsonRpcNotification { Method = method, Params = parameters },
            LspJson.Default.JsonRpcNotification, cancellationToken);

    private Task SendResultAsync<T>(
        JsonElement id, T value, JsonTypeInfo<T> type, CancellationToken cancellationToken) =>
        SendAsync(new JsonRpcSuccess { Id = id, Result = LspJson.ToElement(value, type) },
            LspJson.Default.JsonRpcSuccess, cancellationToken);

    /// <summary>The answer to <c>shutdown</c>: a result that is present and null.</summary>
    private Task SendResultAsync(JsonElement id, CancellationToken cancellationToken) =>
        SendAsync(new JsonRpcSuccess { Id = id, Result = null },
            LspJson.Default.JsonRpcSuccess, cancellationToken);

    private Task SendErrorAsync(
        JsonElement? id, int code, string message, CancellationToken cancellationToken) =>
        SendAsync(
            new JsonRpcFailure { Id = id, Error = new JsonRpcError { Code = code, Message = message } },
            LspJson.Default.JsonRpcFailure, cancellationToken);

    private Task SendAsync<T>(T value, JsonTypeInfo<T> type, CancellationToken cancellationToken) =>
        _connection.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value, type), cancellationToken);

    public void Dispose()
    {
        _analysis.Dispose();
        _connection.Dispose();
    }
}
