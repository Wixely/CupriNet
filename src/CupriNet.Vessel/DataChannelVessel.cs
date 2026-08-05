using System.Net;
using CupriNet.Codex;

namespace CupriNet.Vessel;

/// <summary>
/// A reliable, ordered, <b>message-oriented</b> duplex — e.g. a WebRTC DataChannel (or a WebSocket). Unlike a byte
/// stream it preserves message boundaries, so a <see cref="DataChannelVessel"/> over it needs no length framing.
/// Concrete transports (a SIPSorcery DataChannel, …) implement this minimal contract and CupriNet does the rest.
/// </summary>
public interface IDataChannel : IAsyncDisposable
{
    /// <summary>The remote peer's endpoint, if known.</summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>This side's local endpoint, if known.</summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>Sends one whole message. The channel preserves the boundary.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);

    /// <summary>Receives the next whole message, or <c>null</c> when the channel closes.</summary>
    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts an <see cref="IDataChannel"/> to CupriNet's multiplexed <see cref="IVessel"/>: each frame is exactly one
/// channel message shaped <c>[varuint streamId][payload]</c>. Because the channel already preserves boundaries, no
/// length prefix is needed — which is precisely why a WebRTC DataChannel drops straight into the transport-agnostic
/// <c>ConjoinOverVesselAsync</c> seam and the Toll / Noise / Consecration / Rites run over it unchanged. See
/// <c>design/webrtc-clients.md</c>.
/// </summary>
public sealed class DataChannelVessel(IDataChannel channel) : IVessel
{
    private readonly IDataChannel _channel = channel ?? throw new ArgumentNullException(nameof(channel));

    public EndPoint? RemoteEndPoint => _channel.RemoteEndPoint;
    public EndPoint? LocalEndPoint => _channel.LocalEndPoint;

    public ValueTask SendAsync(ushort streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var header = new CodexWriter();
        header.WriteVarUInt(streamId);
        var prefix = header.WrittenSpan;

        var buffer = new byte[prefix.Length + payload.Length];
        prefix.CopyTo(buffer);
        payload.Span.CopyTo(buffer.AsSpan(prefix.Length));
        return _channel.SendAsync(buffer, cancellationToken);
    }

    public async ValueTask<VesselFrame?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var message = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (message is null)
            return null; // channel closed

        var reader = new CodexReader(message);
        var rawStreamId = reader.ReadVarUInt();
        if (rawStreamId > ushort.MaxValue)
            throw new CodexFormatException($"Stream id {rawStreamId} is out of range.");

        var payload = message.AsSpan(reader.Position).ToArray();
        return new VesselFrame((ushort)rawStreamId, payload);
    }

    public ValueTask DisposeAsync() => _channel.DisposeAsync();
}
