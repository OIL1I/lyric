using System.Buffers;
using System.Globalization;
using System.Text;

namespace Lyric.Lsp.Protocol;

/// <summary>Raised when the byte stream stops being a sequence of framed messages. It is not
/// recoverable: the reader no longer knows where the next message begins.</summary>
public sealed class LspProtocolException(string message) : Exception(message);

/// <summary>
/// The base protocol: a header block, a blank line, then exactly as many bytes of body as the
/// header announced.
///
/// <para>This class knows nothing about JSON-RPC. It hands out and takes in byte payloads, so the
/// one thing that can desynchronise the stream — the framing — has a single implementation and a
/// test of its own.</para>
///
/// <para>The header is read ONE BYTE AT A TIME, which is the reason the input is wrapped in a
/// <see cref="BufferedStream"/>. A read of a fixed block would consume part of the body along with
/// the header, and the body is the one thing whose length must be honoured exactly.</para>
///
/// <para>Writing is SERIALISED. Diagnostics are published from analysis tasks while responses are
/// written from the read loop, and two interleaved payloads do not produce two damaged messages —
/// they produce a stream whose next header lands in the middle of a body, which no client
/// recovers from.</para>
/// </summary>
public sealed class LspConnection : IDisposable
{
    private const string ContentLengthHeader = "Content-Length";

    /// <summary>An upper bound on one message. A corrupt or hostile length would otherwise ask
    /// for an allocation the process cannot make, and the failure would look like a crash rather
    /// than like the protocol fault it is.</summary>
    private const int MaxContentLength = 32 * 1024 * 1024;

    /// <summary>A header line long enough for any real header and short enough that a stream of
    /// bytes without a newline in it fails instead of growing until memory runs out.</summary>
    private const int MaxHeaderLineLength = 8 * 1024;

    private readonly BufferedStream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LspConnection(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        _input = new BufferedStream(input);
        _output = output;
    }

    /// <summary>
    /// The next message body, or <c>null</c> at end of stream.
    ///
    /// <para>End of stream BETWEEN messages is the normal way an editor goes away and is not an
    /// error. End of stream INSIDE a message is <see cref="LspProtocolException"/>: the sender
    /// announced bytes it then did not send.</para>
    /// </summary>
    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        var contentLength = -1;
        var sawHeader = false;

        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellationToken).ConfigureAwait(false);

            // Only here is the end of the stream harmless: nothing of a message has been read yet.
            if (line is null) return sawHeader
                ? throw new LspProtocolException("stream ended inside a header block")
                : null;

            if (line.Length == 0) break; // the blank line closes the header block
            sawHeader = true;

            var separator = line.IndexOf(':');
            if (separator < 0)
                throw new LspProtocolException($"header without a colon: '{line}'");

            var name = line[..separator].Trim();
            if (!name.Equals(ContentLengthHeader, StringComparison.OrdinalIgnoreCase))
                continue; // Content-Type is the only other one the specification defines, and it carries nothing we act on

            var value = line[(separator + 1)..].Trim();
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength))
                throw new LspProtocolException($"{ContentLengthHeader} is not a number: '{value}'");

            if (contentLength > MaxContentLength)
                throw new LspProtocolException($"{ContentLengthHeader} of {contentLength} exceeds the limit");
        }

        if (contentLength < 0)
            throw new LspProtocolException($"header block without {ContentLengthHeader}");

        var body = new byte[contentLength];
        try
        {
            await _input.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new LspProtocolException(
                $"stream ended after fewer than the announced {contentLength} bytes");
        }

        return body;
    }

    /// <summary>
    /// Frames a payload and writes it.
    ///
    /// <para>The header is ASCII by specification, so its length in bytes is its length in
    /// characters; the body is counted in BYTES rather than in characters, which is the difference
    /// that breaks every implementation that forgets it the moment a message carries a non-ASCII
    /// identifier.</para>
    /// </summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes(
            $"{ContentLengthHeader}: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// One header line without its terminator, <c>null</c> at end of stream.
    ///
    /// <para>A lone <c>\n</c> terminates as well as <c>\r\n</c>. The specification writes
    /// <c>\r\n</c>, and accepting the shorter form costs nothing: a header value never contains a
    /// newline, so there is no message the tolerance could read differently.</para>
    /// </summary>
    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(256);
        var length = 0;
        var single = new byte[1];

        try
        {
            while (true)
            {
                if (await _input.ReadAsync(single, cancellationToken).ConfigureAwait(false) == 0)
                    return length == 0 ? null : throw new LspProtocolException(
                        "stream ended inside a header line");

                var b = single[0];
                if (b == (byte)'\n')
                {
                    if (length > 0 && buffer[length - 1] == (byte)'\r') length--;
                    return Encoding.ASCII.GetString(buffer, 0, length);
                }

                if (length == MaxHeaderLineLength)
                    throw new LspProtocolException("header line without a terminator");

                if (length == buffer.Length)
                {
                    var grown = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, length).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                buffer[length++] = b;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _input.Dispose();
    }
}
