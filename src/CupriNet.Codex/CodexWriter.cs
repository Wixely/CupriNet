using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace CupriNet.Codex;

/// <summary>
/// Writes the canonical binary form used for all CupriNet documents and frames. Integers are
/// big-endian; byte strings and text are length-prefixed with an unsigned LEB128 varint. The encoding
/// is deterministic so that signed documents have a single canonical byte representation.
/// </summary>
public sealed class CodexWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public int Length => _buffer.WrittenCount;

    public void WriteByte(byte value)
    {
        var span = _buffer.GetSpan(1);
        span[0] = value;
        _buffer.Advance(1);
    }

    public void WriteUInt32(uint value)
    {
        var span = _buffer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32BigEndian(span, value);
        _buffer.Advance(sizeof(uint));
    }

    public void WriteUInt64(ulong value)
    {
        var span = _buffer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64BigEndian(span, value);
        _buffer.Advance(sizeof(ulong));
    }

    /// <summary>Writes an unsigned LEB128 varint.</summary>
    public void WriteVarUInt(ulong value)
    {
        while (value >= 0x80)
        {
            WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        WriteByte((byte)value);
    }

    /// <summary>Writes a length-prefixed byte string.</summary>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        WriteVarUInt((ulong)value.Length);
        var span = _buffer.GetSpan(value.Length);
        value.CopyTo(span);
        _buffer.Advance(value.Length);
    }

    /// <summary>Writes a length-prefixed UTF-8 string.</summary>
    public void WriteString(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteVarUInt((ulong)byteCount);
        var span = _buffer.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(value, span);
        _buffer.Advance(byteCount);
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;
}
