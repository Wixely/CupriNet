using System.Net;
using System.Net.Sockets;
using CupriNet.Codex;

namespace CupriNet.Vessel;

/// <summary>A single multiplexed frame received on a Vessel: the logical stream id and its payload.</summary>
public readonly record struct VesselFrame(ushort StreamId, byte[] Payload);

/// <summary>
/// A connected transport session between two nodes. Many independent logical streams are multiplexed
/// over one connection: every frame carries a stream id so protocols (Epistles, Conduits, Reliquaries)
/// share a Vessel without head-of-line coupling at the framing layer.
/// </summary>
/// <remarks>
/// Phase 1 carries frames in the clear over TCP. The Noise session is layered in later without changing
/// this framing: payloads become AEAD-sealed, but the stream-id + length-prefixed shape is unchanged.
/// </remarks>
public sealed class Vessel : IVessel
{
    private readonly Stream _stream;
    private readonly EndPoint? _localEndPoint;
    private readonly EndPoint? _remoteEndPoint;
    private readonly Func<ValueTask>? _onDispose;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly int _maxFrameSize;

    /// <summary>
    /// Wraps any duplex byte stream (TCP's NetworkStream, or an <see cref="ArqStream"/> over reliable UDP) as a
    /// framed, multiplexed session. The framing is identical regardless of the underlying transport.
    /// </summary>
    internal Vessel(Stream stream, EndPoint? localEndPoint, EndPoint? remoteEndPoint, int maxFrameSize, Func<ValueTask>? onDispose = null)
    {
        _stream = stream;
        _localEndPoint = localEndPoint;
        _remoteEndPoint = remoteEndPoint;
        _maxFrameSize = maxFrameSize;
        _onDispose = onDispose;
    }

    /// <summary>The remote peer's endpoint, if connected.</summary>
    public EndPoint? RemoteEndPoint => _remoteEndPoint;

    /// <summary>This side's local endpoint, if connected.</summary>
    public EndPoint? LocalEndPoint => _localEndPoint;

    /// <summary>Sends a payload on a logical stream. Writes are serialized so frames never interleave.</summary>
    public async ValueTask SendAsync(ushort streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var writer = new CodexWriter();
        writer.WriteVarUInt(streamId);
        writer.WriteBytes(payload.Span);
        var frame = FrameCodec.Encode(writer.WrittenSpan, _maxFrameSize);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Receives the next frame, or <c>null</c> when the peer closes the connection cleanly.</summary>
    public async ValueTask<VesselFrame?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var payload = await FrameCodec.ReadAsync(_stream, _maxFrameSize, cancellationToken).ConfigureAwait(false);
        if (payload is null)
            return null;

        var reader = new CodexReader(payload);
        var rawStreamId = reader.ReadVarUInt();
        if (rawStreamId > ushort.MaxValue)
            throw new CodexFormatException($"Stream id {rawStreamId} is out of range.");
        var data = reader.ReadBytes().ToArray();
        return new VesselFrame((ushort)rawStreamId, data);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_onDispose is not null)
                await _onDispose().ConfigureAwait(false);
            _writeLock.Dispose();
        }
    }
}
