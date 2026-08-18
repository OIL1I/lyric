using Lyric.Core;

namespace Lyric.Tests.Core;

/// <summary>
/// The footer contract of <c>docs/Pack.md</c>: what the writer appends, the reader finds, and the
/// three answers — absent, damaged, present — stay distinct. The packer and the stub test the
/// contract end to end in <c>Lyric.Tests.Cli</c>; here the bytes themselves are the subject.
/// </summary>
public class PackFooterTests
{
    private static MemoryStream Packed(int stubBytes, int payloadBytes)
    {
        var stream = new MemoryStream();
        stream.Write(new byte[stubBytes]);
        stream.Write(new byte[payloadBytes]);
        PackFooter.Write(stream, payloadBytes);
        return stream;
    }

    [Fact]
    public void A_written_footer_reads_back()
    {
        using var stream = Packed(stubBytes: 100, payloadBytes: 40);

        Assert.Equal(PackFooterState.Present, PackFooter.TryRead(stream, out var payload));
        Assert.Equal(100, payload.Offset);
        Assert.Equal(40, payload.Length);
    }

    [Fact]
    public void The_footer_is_exactly_its_declared_size()
    {
        using var stream = Packed(stubBytes: 10, payloadBytes: 5);
        Assert.Equal(10 + 5 + PackFooter.Size, stream.Length);
    }

    [Fact]
    public void A_file_without_magic_is_absent_not_damaged()
    {
        // Longer than a footer, so the answer comes from the content, not the length.
        using var stream = new MemoryStream(new byte[200]);
        Assert.Equal(PackFooterState.Absent, PackFooter.TryRead(stream, out _));
    }

    [Fact]
    public void A_file_shorter_than_a_footer_is_absent()
    {
        using var stream = new MemoryStream(new byte[PackFooter.Size - 1]);
        Assert.Equal(PackFooterState.Absent, PackFooter.TryRead(stream, out _));
    }

    [Fact]
    public void A_truncated_pack_loses_its_footer_and_reads_as_absent()
    {
        // Truncation cuts the tail, where the magic lives. The stub then reports an empty (or
        // foreign) executable — there is nothing left that SAYS a program was ever there.
        using var whole = Packed(stubBytes: 100, payloadBytes: 40);
        using var truncated = new MemoryStream(whole.ToArray()[..^10]);

        Assert.Equal(PackFooterState.Absent, PackFooter.TryRead(truncated, out _));
    }

    [Fact]
    public void A_length_reaching_outside_the_file_is_damaged()
    {
        // The magic is intact, the length is a lie: bytes vanished BEFORE the footer, or the
        // field was corrupted. Either way running "the payload" would execute garbage.
        var bytes = Packed(stubBytes: 0, payloadBytes: 10).ToArray();
        BitConverter.GetBytes(1000UL).CopyTo(bytes, bytes.Length - 16);

        using var stream = new MemoryStream(bytes);
        Assert.Equal(PackFooterState.Damaged, PackFooter.TryRead(stream, out _));
    }

    [Fact]
    public void A_zero_length_is_damaged()
    {
        var bytes = Packed(stubBytes: 10, payloadBytes: 10).ToArray();
        BitConverter.GetBytes(0UL).CopyTo(bytes, bytes.Length - 16);

        using var stream = new MemoryStream(bytes);
        Assert.Equal(PackFooterState.Damaged, PackFooter.TryRead(stream, out _));
    }

    [Fact]
    public void An_unknown_footer_version_is_damaged()
    {
        var bytes = Packed(stubBytes: 10, payloadBytes: 10).ToArray();
        BitConverter.GetBytes(2u).CopyTo(bytes, bytes.Length - PackFooter.Size);

        using var stream = new MemoryStream(bytes);
        Assert.Equal(PackFooterState.Damaged, PackFooter.TryRead(stream, out _));
    }

    [Fact]
    public void The_reserved_field_is_ignored_on_read()
    {
        // Written as zero today; a future minor may use it. A reader that rejected it would
        // make the field unusable — the same rule the bytecode reader follows for unknown
        // sections.
        var bytes = Packed(stubBytes: 10, payloadBytes: 10).ToArray();
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(bytes, bytes.Length - 20);

        using var stream = new MemoryStream(bytes);
        Assert.Equal(PackFooterState.Present, PackFooter.TryRead(stream, out _));
    }

    [Fact]
    public void The_writer_refuses_an_empty_payload()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => PackFooter.Write(stream, 0));
    }
}
