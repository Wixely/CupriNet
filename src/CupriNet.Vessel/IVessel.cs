using System.Net;

namespace CupriNet.Vessel;

/// <summary>
/// A connected, multiplexed transport session: send and receive stream-tagged frames. Implemented by the
/// raw <see cref="Vessel"/> (plaintext TCP) and by wrappers such as a Noise-encrypting vessel, so higher
/// layers work over either without change.
/// </summary>
public interface IVessel : IAsyncDisposable
{
    /// <summary>The remote peer's endpoint, if connected.</summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>Sends a payload on a logical stream.</summary>
    ValueTask SendAsync(ushort streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>Receives the next frame, or null when the peer closes the connection.</summary>
    ValueTask<VesselFrame?> ReceiveAsync(CancellationToken cancellationToken = default);
}
