using System.Buffers.Binary;

namespace Lyric.Core;

/// <summary>Where a packed program lies inside its executable: <see cref="Offset"/> bytes from the
/// start, <see cref="Length"/> bytes long.</summary>
public readonly record struct PackPayload(long Offset, long Length);

/// <summary>What reading the tail of an executable found.</summary>
public enum PackFooterState
{
    /// <summary>No footer: the file is a bare stub, or not a packed program at all.</summary>
    Absent,

    /// <summary>The magic is there but the rest does not hold together — a truncated copy, or a
    /// version this reader does not know.</summary>
    Damaged,

    /// <summary>A payload is present and its bounds lie inside the file.</summary>
    Present,
}

/// <summary>
/// The trailer of a packed executable, specified in <c>docs/Pack.md</c>.
///
/// <para>A packed program is the stub executable with the <c>.lyrbc</c> module appended and this
/// footer after it. The footer sits at the very END of the file so the stub finds the payload
/// without knowing anything about its own size: read the last <see cref="Size"/> bytes, check the
/// magic, and the length names the payload directly before the footer.</para>
///
/// <para>It lives in <c>Lyric.Core</c> because writer (<c>lyrpack</c>) and reader
/// (<c>lyrstub</c>) must agree byte for byte, and Core is the one project every binary
/// shares.</para>
/// </summary>
public static class PackFooter
{
    /// <summary>Fixed layout, little-endian: <c>u32 version</c>, <c>u32 reserved</c> (written as
    /// zero, ignored on read), <c>u64 payload length</c>, 8 bytes magic.</summary>
    public const int Size = 24;

    /// <summary>Bumped only when the footer LAYOUT changes. The payload's own format has its own
    /// version inside the <c>.lyrbc</c> header and is none of the footer's business.</summary>
    public const uint Version = 1;

    private static ReadOnlySpan<byte> Magic => "LYRPACK1"u8;

    /// <summary>Appends the footer for a payload of <paramref name="payloadLength"/> bytes to
    /// wherever the stream currently stands.</summary>
    public static void Write(Stream stream, long payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(payloadLength);

        Span<byte> footer = stackalloc byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(footer, Version);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[4..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(footer[8..], (ulong)payloadLength);
        Magic.CopyTo(footer[16..]);
        stream.Write(footer);
    }

    /// <summary>
    /// Reads the footer from the end of <paramref name="stream"/>.
    ///
    /// <para>Absent and damaged are distinct answers: a bare stub is a working executable that
    /// carries no program yet, a damaged pack is a broken copy of one that did. The messages for
    /// the two must not be the same.</para>
    /// </summary>
    public static PackFooterState TryRead(Stream stream, out PackPayload payload)
    {
        payload = default;
        if (stream.Length < Size) return PackFooterState.Absent;

        Span<byte> footer = stackalloc byte[Size];
        stream.Seek(-Size, SeekOrigin.End);
        stream.ReadExactly(footer);

        if (!footer[16..].SequenceEqual(Magic)) return PackFooterState.Absent;

        var version = BinaryPrimitives.ReadUInt32LittleEndian(footer);
        var length = BinaryPrimitives.ReadUInt64LittleEndian(footer[8..]);

        // An unknown version is damage, not absence: the magic says a payload is there, and
        // running the stub as if it were empty would hide it.
        if (version != Version) return PackFooterState.Damaged;
        if (length == 0 || length > (ulong)(stream.Length - Size)) return PackFooterState.Damaged;

        payload = new PackPayload(stream.Length - Size - (long)length, (long)length);
        return PackFooterState.Present;
    }
}
