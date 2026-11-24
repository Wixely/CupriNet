using System.Buffers.Binary;
using System.Text;

namespace CupriNet.Codex;

/// <summary>
/// Reads the canonical binary form produced by <see cref="CodexWriter"/>. All reads are bounds-checked;
/// a malformed document throws <see cref="CodexFormatException"/> rather than over-reading. Only the canonical
/// form is accepted — a varint must be in its shortest encoding and text must be well-formed UTF-8 — so one
/// value has one byte representation on the way in as well as on the way out.
/// </summary>
public ref struct CodexReader(ReadOnlySpan<byte> data)
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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

    /// <summary>Reads an unsigned LEB128 varint (at most 10 bytes) in its shortest encoding.</summary>
    public ulong ReadVarUInt()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (shift >= 64)
                throw new CodexFormatException("Varint is too long.");
            var b = ReadByte();
            if (shift == 63 && (b & 0x7E) != 0)
                throw new CodexFormatException("Varint overflows 64 bits.");
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                if (b == 0 && shift > 0)
                    throw new CodexFormatException("Varint is not in its shortest encoding.");
                return result;
            }
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
    public string ReadString()
    {
        var bytes = ReadBytes();
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new CodexFormatException("Text field is not well-formed UTF-8."); }
    }

    private readonly void EnsureAvailable(int count)
    {
        if (Remaining < count)
            throw new CodexFormatException($"Unexpected end of data: needed {count} bytes, had {Remaining}.");
    }
}
