using System.Buffers.Binary;
using System.Text;

namespace Lyric.Bytecode.Encoding;

/// <summary>
/// Schreib-Primitiven des Formats. Alles, was <c>docs/Bytecode.md</c> als Kodierung festlegt,
/// steht hier — und nur hier.
///
/// <para>Plattformneutral per ADR-013: Fixbreiten explizit little-endian (nicht die Host-Byte-
/// Reihenfolge), variable Ganzzahlen als LEB128, Strings als längenpräfigiertes UTF-8, Floats als
/// IEEE-754-Bitmuster. Nichts davon darf von <c>BitConverter</c>-Defaults abhängen, sonst
/// unterscheidet sich der Output auf einer Big-Endian-Maschine.</para>
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

    /// <summary>LEB128, unsigned. Sieben Nutzbits pro Byte, das achte ist das Fortsetzungs-Bit.</summary>
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

    /// <summary>IEEE-754-Bitmuster, little-endian. Nicht der Dezimalwert: nur das Bitmuster ist
    /// verlustfrei und deterministisch (NaN-Nutzlast inklusive).</summary>
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

    /// <summary>Länge in Bytes (nicht in Zeichen) als uleb128, dann die UTF-8-Bytes.</summary>
    public void String(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        ULeb(bytes.Length);
        _buffer.AddRange(bytes);
    }

    public void Raw(ReadOnlySpan<byte> bytes) => _buffer.AddRange(bytes);

    public void Raw(byte[] bytes) => _buffer.AddRange(bytes);
}
