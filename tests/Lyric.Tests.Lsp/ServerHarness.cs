using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Lyric.Lsp;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// A server on a pair of in-memory pipes, driven from the test as a client would drive it.
///
/// <para>Pipes rather than a <see cref="MemoryStream"/>: a memory stream at its end reports end of
/// file, so the server would shut down before the test had said anything. A pipe blocks, which is
/// what a real connection does between two messages.</para>
///
/// <para>The client side reuses <see cref="LspConnection"/>. That makes the framing untested BY
/// THESE TESTS — a reader and a writer that are wrong in the same way would agree with each other.
/// It is tested against hand-written bytes in <see cref="FramingTests"/> instead, in both
/// directions.</para>
/// </summary>
internal sealed class ServerHarness : IAsyncDisposable
{
    /// <summary>Every wait is bounded. A server that never answers must fail the test rather than
    /// hang the run.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Pipe _toServer = new();
    private readonly Pipe _fromServer = new();
    private readonly LspConnection _client;
    private readonly LspServer _server;
    private readonly Task<int> _running;
    private int _nextId = 1;

    public ServerHarness(LspServerOptions? options = null)
    {
        _server = new LspServer(
            _toServer.Reader.AsStream(),
            _fromServer.Writer.AsStream(),
            options ?? new LspServerOptions { Debounce = TimeSpan.Zero });

        _client = new LspConnection(_fromServer.Reader.AsStream(), _toServer.Writer.AsStream());
        _running = _server.RunAsync();
    }

    /// <summary>Sends a raw JSON message and returns the id it was given, if any.</summary>
    public async Task SendAsync(string json)
    {
        using var cancel = new CancellationTokenSource(Timeout);
        await _client.WriteAsync(Encoding.UTF8.GetBytes(json), cancel.Token);
    }

    public async Task<int> RequestAsync(string method, string? parameters = null)
    {
        var id = _nextId++;
        var payload = parameters is null ? string.Empty : $@",""params"":{parameters}";
        await SendAsync($@"{{""jsonrpc"":""2.0"",""id"":{id},""method"":""{method}""{payload}}}");
        return id;
    }

    public Task NotifyAsync(string method, string? parameters = null)
    {
        var payload = parameters is null ? string.Empty : $@",""params"":{parameters}";
        return SendAsync($@"{{""jsonrpc"":""2.0"",""method"":""{method}""{payload}}}");
    }

    /// <summary>The next message from the server, whatever it is.</summary>
    public async Task<JsonElement> ReceiveAsync()
    {
        using var cancel = new CancellationTokenSource(Timeout);
        var payload = await _client.ReadAsync(cancel.Token)
                      ?? throw new InvalidOperationException("the server closed the connection");
        return JsonDocument.Parse(payload).RootElement.Clone();
    }

    /// <summary>
    /// The next message carrying this method, skipping anything before it.
    ///
    /// <para>Log messages arrive unpredictably — the analysis writes them from its own task — and a
    /// test that asserted on the FIRST message would fail on the presence of a log line rather
    /// than on the thing it is about.</para>
    /// </summary>
    public async Task<JsonElement> ReceiveNotificationAsync(string method)
    {
        while (true)
        {
            var message = await ReceiveAsync();
            if (message.TryGetProperty("method", out var name) && name.GetString() == method)
                return message;
        }
    }

    /// <summary>The answer to a request, skipping notifications that overtake it.</summary>
    public async Task<JsonElement> ReceiveResponseAsync(int id)
    {
        while (true)
        {
            var message = await ReceiveAsync();
            if (message.TryGetProperty("id", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.GetInt32() == id)
                return message;
        }
    }

    /// <summary>Runs the handshake and leaves the server ready to take documents.</summary>
    public async Task InitializeAsync()
    {
        var id = await RequestAsync(LspMethods.Initialize, "{}");
        await ReceiveResponseAsync(id);
        await NotifyAsync(LspMethods.Initialized, "{}");
    }

    /// <summary>Closes the client's end of the stream, which is what an editor that crashed looks
    /// like from here.</summary>
    public async Task CloseInputAsync() => await _toServer.Writer.CompleteAsync();

    /// <summary>The process exit code the server run produced.</summary>
    public async Task<int> ExitCodeAsync()
    {
        var code = await _running.WaitAsync(Timeout);

        // Releases any read the test still has outstanding, so a failing assertion after this
        // point reports itself rather than timing out.
        await _fromServer.Writer.CompleteAsync();
        return code;
    }

    public async ValueTask DisposeAsync()
    {
        await _toServer.Writer.CompleteAsync();
        try
        {
            await _running.WaitAsync(Timeout);
        }
        catch (TimeoutException)
        {
            // The assertion that already failed is the more useful report.
        }
        await _fromServer.Writer.CompleteAsync();
        _server.Dispose();
        _client.Dispose();
    }
}
