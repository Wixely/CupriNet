namespace CupriNet.Vessel;

/// <summary>
/// A bidirectional, single logical-stream channel: send and receive length-delimited payloads for one
/// stream id. Implemented directly over a <see cref="Vessel"/> (single-stream use) or via a
/// <see cref="VesselMux"/> (many streams demultiplexed off one connection).
/// </summary>
public interface IStreamChannel
{
    ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>Receives the next payload for this stream, or null when the connection closes.</summary>
    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts a single stream of a <see cref="Vessel"/> as an <see cref="IStreamChannel"/>. Safe only when
/// one stream is read from the Vessel; for concurrent streams use a <see cref="VesselMux"/>.
/// </summary>
public sealed class VesselStreamChannel(IVessel vessel, ushort streamId) : IStreamChannel
{
    private readonly IVessel _vessel = vessel ?? throw new ArgumentNullException(nameof(vessel));

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => _vessel.SendAsync(streamId, payload, cancellationToken);

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var frame = await _vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null)
            return null;
        if (frame.Value.StreamId != streamId)
            throw new InvalidOperationException(
                $"Received a frame on stream {frame.Value.StreamId} but this channel is stream {streamId}. Use a VesselMux to read multiple streams.");
        return frame.Value.Payload;
    }
}
