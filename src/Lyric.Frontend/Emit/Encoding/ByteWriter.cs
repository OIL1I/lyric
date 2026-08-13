using System.Buffers.Binary;
using System.Text;

namespace Lyric.Bytecode.Encoding;

/// <summary>
/// Schreib-Primitiven des Formats. Alles, was <c>docs/Bytecode.md</c> als Kodierung festlegt,
/// lives here, and only here.
///
/// <para>Platform-neutral: fixed widths explicitly little-endian rather than the host byte order,
/// variable integers as LEB128, floats as their IEEE-754 bit pattern. None of it may depend on
/// <c>BitConverter</c> defaults, or the output would differ on a big-endian machine.</para>
/// </summary>
internal sealed class ByteWriter
{
    private readonly List<byte> _buffer = new();

    public int Position => _buffer.Count;

    public byte[] ToArray() => _buffer.ToArray();

    public void U8(byte value) => _buffer.Add(value);

    public void Tag(TypeTag tag) => _buffer.Add((byte)tag);

    public void Opcode(Op op) => _buffer.Add((byte)op);

    public void U16(ushort value)
    {
        Span<byte> tmp = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tmp, value);
        _buffer.AddRange(tmp);
    }

    /// <summary>LEB128, unsigned. Seven payload bits per byte; the eighth is the continuation
    /// bit.</summary>
    public void ULeb(ulong value)
    {
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            _buffer.Add(b);
        } while (value != 0);
    }

    public void ULeb(int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "uleb128 is unsigned");
        ULeb((ulong)value);
    }

    /// <summary>The IEEE-754 bit pattern, little-endian. Not the decimal value: only the bit
    /// pattern is lossless and deterministic, NaN payload included.</summary>
    public void F32(float value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, BitConverter.SingleToUInt32Bits(value));
        _buffer.AddRange(tmp);
    }

    public void F64(double value)
    {
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(tmp, BitConverter.DoubleToUInt64Bits(value));
        _buffer.AddRange(tmp);
    }

    /// <summary>The length in bytes, not characters, as uleb128, then the UTF-8 bytes.</summary>
    public void String(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        ULeb(bytes.Length);
        _buffer.AddRange(bytes);
    }

    public void Raw(ReadOnlySpan<byte> bytes) => _buffer.AddRange(bytes);

    public void Raw(byte[] bytes) => _buffer.AddRange(bytes);
}
