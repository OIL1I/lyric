using System.Text;
using Lyric.Lsp.Protocol;

namespace Lyric.Tests.Lsp;

/// <summary>
/// The base protocol, against hand-written bytes in both directions.
///
/// <para>Written literally rather than produced by the writer and fed to the reader: a reader and
/// a writer that share a fault agree with each other perfectly, and every message that crosses the
/// wire in the rest of this suite goes through both.</para>
///
/// <para>This is the one layer where an error is unrecoverable. A wrong length does not lose one
/// message — it makes the next header land inside a body, and everything after that is noise.
/// </para>
/// </summary>
public sealed class FramingTests
{
    private static LspConnection Reading(byte[] input) =>
        new(new MemoryStream(input), Stream.Null);

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    [Fact]
    public async Task Reads_a_message_framed_exactly_as_the_specification_writes_it()
    {
        using var connection = Reading(Ascii("Content-Length: 2\r\n\r\n{}"));

        var payload = await connection.ReadAsync(CancellationToken.None);

        Assert.Equal("{}", Encoding.UTF8.GetString(payload!));
    }

    [Fact]
    public async Task Reads_two_messages_from_one_stream()
    {
        // The point of the length is that the reader knows where the second one starts without
        // looking for a delimiter. A body containing a header-shaped line proves it does not.
        const string body = @"{""a"":""Content-Length: 9""}";
        using var connection = Reading(Ascii(
            $"Content-Length: {body.Length}\r\n\r\n{body}Content-Length: 2\r\n\r\n[]"));

        var first = await connection.ReadAsync(CancellationToken.None);
        var second = await connection.ReadAsync(CancellationToken.None);

        Assert.Equal(body, Encoding.UTF8.GetString(first!));
        Assert.Equal("[]", Encoding.UTF8.GetString(second!));
    }

    [Fact]
    public async Task Accepts_a_bare_newline_as_a_header_terminator()
    {
        // Not what the specification writes, and cheap to allow: a header value never contains a
        // newline, so there is no message this tolerance could read differently.
        using var connection = Reading(Ascii("Content-Length: 2\n\n{}"));

        var payload = await connection.ReadAsync(CancellationToken.None);

        Assert.Equal("{}", Encoding.UTF8.GetString(payload!));
    }

    [Fact]
    public async Task Ignores_headers_it_does_not_act_on()
    {
        using var connection = Reading(Ascii(
            "Content-Type: application/vscode-jsonrpc; charset=utf-8\r\nContent-Length: 2\r\n\r\n{}"));

        var payload = await connection.ReadAsync(CancellationToken.None);

        Assert.Equal("{}", Encoding.UTF8.GetString(payload!));
    }

    [Fact]
    public async Task Reads_a_message_that_arrives_one_byte_at_a_time()
    {
        // How a message actually arrives on a socket. A reader that assumes one read yields a whole
        // header passes every other test in this file and fails in production.
        using var connection = new LspConnection(
            new DribblingStream(Ascii("Content-Length: 2\r\n\r\n{}")), Stream.Null);

        var payload = await connection.ReadAsync(CancellationToken.None);

        Assert.Equal("{}", Encoding.UTF8.GetString(payload!));
    }

    [Fact]
    public async Task Counts_the_body_in_bytes_and_not_in_characters()
    {
        // Two characters, four bytes. The reader that counts characters stops halfway through the
        // second one and starts the next header inside a UTF-8 sequence.
        var body = Encoding.UTF8.GetBytes("\"üö\"");
        var message = Ascii($"Content-Length: {body.Length}\r\n\r\n").Concat(body).ToArray();
        using var connection = Reading(message);

        var payload = await connection.ReadAsync(CancellationToken.None);

        Assert.Equal("\"üö\"", Encoding.UTF8.GetString(payload!));
    }

    [Fact]
    public async Task End_of_stream_between_messages_is_not_an_error()
    {
        using var connection = Reading([]);

        Assert.Null(await connection.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task End_of_stream_inside_a_body_is_a_protocol_error()
    {
        // The sender announced ten bytes and sent two. Silence here would hand a truncated message
        // to the parser and report it as invalid JSON — a diagnosis about the wrong layer.
        using var connection = Reading(Ascii("Content-Length: 10\r\n\r\n{}"));

        await Assert.ThrowsAsync<LspProtocolException>(
            () => connection.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task End_of_stream_inside_a_header_block_is_a_protocol_error()
    {
        using var connection = Reading(Ascii("Content-Length: 2\r\n"));

        await Assert.ThrowsAsync<LspProtocolException>(
            () => connection.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_header_block_without_a_length_is_a_protocol_error()
    {
        using var connection = Reading(Ascii("Content-Type: text/plain\r\n\r\n{}"));

        await Assert.ThrowsAsync<LspProtocolException>(
            () => connection.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("Content-Length: eleven\r\n\r\n{}")]
    [InlineData("Content-Length: -4\r\n\r\n{}")]
    [InlineData("Content-Length 2\r\n\r\n{}")]
    public async Task A_malformed_header_is_a_protocol_error(string input)
    {
        using var connection = Reading(Ascii(input));

        await Assert.ThrowsAsync<LspProtocolException>(
            () => connection.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Writes_the_header_the_specification_asks_for()
    {
        var output = new MemoryStream();
        using var connection = new LspConnection(Stream.Null, output);

        await connection.WriteAsync(Encoding.UTF8.GetBytes("{}"),
            CancellationToken.None);

        Assert.Equal("Content-Length: 2\r\n\r\n{}", Encoding.ASCII.GetString(output.ToArray()));
    }

    [Fact]
    public async Task Writes_a_length_in_bytes_for_a_body_that_is_not_ascii()
    {
        var output = new MemoryStream();
        using var connection = new LspConnection(Stream.Null, output);

        await connection.WriteAsync(Encoding.UTF8.GetBytes("\"ü\""),
            CancellationToken.None);

        // Three characters, four bytes.
        Assert.StartsWith("Content-Length: 4\r\n\r\n", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task Concurrent_writes_do_not_interleave()
    {
        // The reason the write path holds a lock. Diagnostics are published from analysis tasks
        // while responses go out from the read loop, and two payloads woven together do not
        // produce two damaged messages but an unreadable stream.
        var output = new MemoryStream();
        using var connection = new LspConnection(Stream.Null, new SlowStream(output));

        var bodies = Enumerable.Range(0, 20)
            .Select(i => Encoding.UTF8.GetBytes($@"{{""n"":{i}}}"))
            .ToArray();

        await Task.WhenAll(bodies.Select(body =>
            connection.WriteAsync(body, CancellationToken.None)));

        // Read the result back through the reader: if any two writes interleaved, a length no
        // longer matches its body and the parse fails rather than silently returning something.
        using var reader = new LspConnection(new MemoryStream(output.ToArray()), Stream.Null);
        var seen = new List<string>();
        while (await reader.ReadAsync(CancellationToken.None) is { } payload)
            seen.Add(Encoding.UTF8.GetString(payload));

        Assert.Equal(bodies.Length, seen.Count);
        Assert.Equal(
            bodies.Select(Encoding.UTF8.GetString).OrderBy(text => text, StringComparer.Ordinal),
            seen.OrderBy(text => text, StringComparer.Ordinal));
    }

    /// <summary>A stream that yields one byte per read, however much was asked for.</summary>
    private sealed class DribblingStream(byte[] content) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= content.Length || count == 0) return 0;
            buffer[offset] = content[_position++];
            return 1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A stream that yields between the header write and the body write.
    ///
    /// <para>Without it the concurrency test proves nothing: the writes complete synchronously and
    /// never overlap, so an unlocked implementation passes.</para>
    /// </summary>
    private sealed class SlowStream(Stream inner) : Stream
    {
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
