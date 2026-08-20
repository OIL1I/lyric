using System.Collections.Concurrent;
using System.Text.Json;
using Lyric.Dap;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Dap;

/// <summary>
/// An in-memory one-way byte stream: what one side writes, the other reads, in order. Two of
/// them make the duplex the adapter normally gets from stdio.
/// </summary>
internal sealed class SimplexStream : Stream
{
    private readonly ConcurrentQueue<byte[]> _chunks = new();
    private readonly SemaphoreSlim _available = new(0);
    private byte[]? _current;
    private int _offset;
    private volatile bool _completed;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count == 0) return;
        var copy = new byte[count];
        Array.Copy(buffer, offset, copy, 0, count);
        _chunks.Enqueue(copy);
        _available.Release();
    }

    /// <summary>Ends the stream: readers see end-of-stream once the queue drains.</summary>
    public void Complete()
    {
        _completed = true;
        _available.Release();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_current is not null && _offset < _current.Length)
            {
                var count = Math.Min(buffer.Length, _current.Length - _offset);
                _current.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            if (_chunks.TryDequeue(out _current)) { _offset = 0; continue; }
            _current = null;
            if (_completed && _chunks.IsEmpty) return 0;
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// Drives a <see cref="DapServer"/> in-process: requests go in with ascending sequence numbers,
/// and everything coming back — responses and events — lands in one queue the assertions pull
/// from, each with a timeout so a protocol bug fails instead of hanging the suite.
/// </summary>
internal sealed class DapTestClient : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly SimplexStream _toServer = new();
    private readonly SimplexStream _fromServer = new();
    private readonly LspConnection _client;
    private readonly Task _server;
    private readonly Task _reader;
    private readonly BlockingCollection<DapMessage> _incoming = new();
    private readonly List<DapMessage> _pending = new();
    private int _sequence;

    public DapTestClient(string stdlibRoot)
    {
        _client = new LspConnection(_fromServer, _toServer);
        var server = new DapServer(_toServer, _fromServer,
            new DapServerOptions { StdlibRoot = stdlibRoot });
        _server = Task.Run(() => server.RunAsync());
        _reader = Task.Run(ReadAllAsync);
    }

    private async Task ReadAllAsync()
    {
        while (true)
        {
            var payload = await _client.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            if (payload is null) break;
            var message = JsonSerializer.Deserialize<DapMessage>(payload, DapJson.Options);
            if (message is not null) _incoming.Add(message);
        }
        _incoming.CompleteAdding();
    }

    public async Task<DapMessage> RequestAsync(string command, object? arguments = null)
    {
        var seq = ++_sequence;
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            seq,
            type = "request",
            command,
            arguments,
        }, DapJson.Options);
        await _client.WriteAsync(json, CancellationToken.None);

        return Take(m => m.Type == "response" && m.RequestSeq == seq,
            $"response to '{command}'");
    }

    public DapMessage TakeEvent(string name) =>
        Take(m => m.Type == "event" && m.Event == name, $"event '{name}'");

    /// <summary>The first queued or incoming message matching; everything skipped stays queued
    /// for a later expectation, because events and responses interleave freely.</summary>
    private DapMessage Take(Func<DapMessage, bool> matches, string what)
    {
        var found = _pending.FirstOrDefault(matches);
        if (found is not null)
        {
            _pending.Remove(found);
            return found;
        }

        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!_incoming.TryTake(out var message, TimeSpan.FromMilliseconds(250))) continue;
            if (matches(message)) return message;
            _pending.Add(message);
        }

        throw new TimeoutException(
            $"no {what} arrived; pending: [{string.Join(", ",
                _pending.Select(m => m.Type == "event" ? m.Event : m.Command))}]");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await RequestAsync("disconnect");
        }
        catch (TimeoutException)
        {
            // The server is already gone; closing the streams below ends the tasks.
        }

        _toServer.Complete();
        _fromServer.Complete();
        await Task.WhenAny(Task.WhenAll(_server, _reader), Task.Delay(Timeout));
    }
}
