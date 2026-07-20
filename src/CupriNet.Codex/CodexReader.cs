using System.Buffers.Binary;
using System.Text;

namespace CupriNet.Codex;

/// <summary>
/// Reads the canonical binary form produced by <see cref="CodexWriter"/>. All reads are bounds-checked;
/// a malformed document throws <see cref="CodexFormatException"/> rather than over-reading.
/// </summary>
public ref struct CodexReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position = 0;

    public readonly int Position => _position;

    public readonly int Remaining => _data.Length - _position;

    public readonly bool End => _position >= _data.Length;

    public byte ReadByte()
    {
        if (_position >= _data.Length)
            throw new CodexFormatException("Unexpected end of data reading a byte.");
        return _data[_position++];
    }

    public uint ReadUInt32()
    {
        EnsureAvailable(sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_position, sizeof(uint)));
        _position += sizeof(uint);
        return value;
    }

    public ulong ReadUInt64()
    {
        EnsureAvailable(sizeof(ulong));
        var value = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(_position, sizeof(ulong)));
        _position += sizeof(ulong);
        return value;
    }

    /// <summary>Reads an unsigned LEB128 varint (at most 10 bytes).</summary>
    public ulong ReadVarUInt()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (shift >= 64)
                throw new CodexFormatException("Varint is too long.");
            var b = ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
        }
    }

    /// <summary>Reads a length-prefixed byte string as a span into the underlying buffer.</summary>
    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = ReadVarUInt();
        if (length > (ulong)Remaining)
            throw new CodexFormatException("Length-prefixed field exceeds the remaining data.");
        var slice = _data.Slice(_position, (int)length);
        _position += (int)length;
        return slice;
    }

    /// <summary>Reads a length-prefixed UTF-8 string.</summary>
    public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

    private readonly void EnsureAvailable(int count)
    {
        if (Remaining < count)
            throw new CodexFormatException($"Unexpected end of data: needed {count} bytes, had {Remaining}.");
    }
}
