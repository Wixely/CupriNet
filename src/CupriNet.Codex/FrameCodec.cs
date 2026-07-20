using System.Buffers.Binary;

namespace CupriNet.Codex;

/// <summary>
/// Length-prefixed framing over a byte stream: a 4-byte big-endian payload length followed by the
/// payload. A hard maximum frame size (a Ward) protects against memory-exhaustion before a single byte
/// of payload is buffered.
/// </summary>
public static class FrameCodec
{
    /// <summary>Default maximum payload size accepted for a single frame (16 MiB).</summary>
    public const int DefaultMaxFrameSize = 16 * 1024 * 1024;

    private const int LengthPrefixSize = sizeof(uint);

    /// <summary>Encodes a payload into a new length-prefixed frame buffer.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> payload, int maxFrameSize = DefaultMaxFrameSize)
    {
        if (payload.Length > maxFrameSize)
            throw new CodexFormatException($"Frame payload of {payload.Length} bytes exceeds the maximum of {maxFrameSize}.");

        var frame = new byte[LengthPrefixSize + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(LengthPrefixSize));
        return frame;
    }

    /// <summary>Reads a single length-prefixed frame payload from a stream, or <c>null</c> at end of stream.</summary>
    public static async ValueTask<byte[]?> ReadAsync(Stream stream, int maxFrameSize = DefaultMaxFrameSize, CancellationToken cancellationToken = default)
    {
        var lengthBuffer = new byte[LengthPrefixSize];
        var read = await ReadUpToAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
        if (read == 0)
            return null;
        if (read < LengthPrefixSize)
            throw new CodexFormatException("Stream ended in the middle of a frame length prefix.");

        var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);
        if (length > (uint)maxFrameSize)
            throw new CodexFormatException($"Declared frame length {length} exceeds the maximum of {maxFrameSize}.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async ValueTask<int> ReadUpToAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }
}
