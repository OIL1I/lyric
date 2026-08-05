using System.Buffers.Binary;

namespace Lyric.Bytecode.Encoding;

/// <summary>
/// Lese-Primitiven, spiegelbildlich zu <c>ByteWriter</c> (der in Lyric.Bytecode.Emit liegt —
/// ADR-017 trennt Lese- und Schreibseite, damit die Runtime die IR nicht mitzieht).
///
/// <para><b>Jede</b> Methode prüft vorher, ob genug Bytes da sind. Der Leser ist die Stelle, an der
/// nicht vertrauenswürdige Bytes ins System kommen — er darf auf keiner Eingabe mit einer
/// <c>IndexOutOfRangeException</c> aussteigen, sondern nur mit
/// <see cref="MalformedBytecodeException"/>.</para>
/// </summary>
internal sealed class ByteReader
{
    private readonly byte[] _bytes;
    private int _position;

    public ByteReader(byte[] bytes, int position = 0)
    {
        _bytes = bytes;
        _position = position;
    }

    public int Position => _position;
    public int Remaining => _bytes.Length - _position;
    public bool AtEnd => _position >= _bytes.Length;

    private void Need(int count)
    {
        if (Remaining < count)
            throw new MalformedBytecodeException(BytecodeDiagnostics.Truncated,
                $"unexpected end of file at byte {_position} (needed {count} more)");
    }

    public byte U8()
    {
        Need(1);
        return _bytes[_position++];
    }

    public ushort U16()
    {
        Need(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(_position, 2));
        _position += 2;
        return value;
    }

    public ulong ULeb()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = U8();
            // 64 Bit brauchen höchstens 10 Gruppen zu 7 Bit. Ohne diese Schranke könnte eine
            // manipulierte Datei den Leser beliebig lange fortsetzen lassen.
            if (shift > 63)
                throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                    $"uleb128 at byte {_position} is longer than 64 bits");

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }

    /// <summary>uleb128, das als Index oder Länge dient — muss in <c>int</c> passen.</summary>
    public int ULebAsCount()
    {
        var value = ULeb();
        if (value > int.MaxValue)
            throw new MalformedBytecodeException(BytecodeDiagnostics.Truncated,
                $"count {value} at byte {_position} is implausibly large");
        return (int)value;
    }

    public float F32()
    {
        Need(4);
        var bits = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(_position, 4));
        _position += 4;
        return BitConverter.UInt32BitsToSingle(bits);
    }

    public double F64()
    {
        Need(8);
        var bits = BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(_position, 8));
        _position += 8;
        return BitConverter.UInt64BitsToDouble(bits);
    }

    public string String()
    {
        var length = ULebAsCount();
        Need(length);
        var value = System.Text.Encoding.UTF8.GetString(_bytes, _position, length);
        _position += length;
        return value;
    }

    public byte[] Raw(int count)
    {
        Need(count);
        var slice = new byte[count];
        Array.Copy(_bytes, _position, slice, 0, count);
        _position += count;
        return slice;
    }

    public void ExpectMagic()
    {
        Need(Format.Magic.Length);
        if (!_bytes.AsSpan(_position, Format.Magic.Length).SequenceEqual(Format.Magic))
            throw new MalformedBytecodeException(BytecodeDiagnostics.BadMagic,
                "not a .lyrbc file (magic 'LYRB' missing)");
        _position += Format.Magic.Length;
    }

    public TypeTag Tag()
    {
        var raw = U8();
        if (!System.Enum.IsDefined(typeof(TypeTag), raw))
            throw new MalformedBytecodeException(BytecodeDiagnostics.UnknownEncoding,
                $"unknown type tag 0x{raw:X2} at byte {_position - 1}");
        return (TypeTag)raw;
    }
}
