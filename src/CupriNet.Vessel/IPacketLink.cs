using System.Net;

namespace CupriNet.Vessel;

/// <summary>
/// An unreliable datagram channel to a single peer — the substrate the <see cref="ReliableArq"/> runs over.
/// Implemented by a connected UDP socket (<see cref="UdpPacketLink"/>) in production and by an in-memory lossy
/// channel in tests, so the reliable transport can be exercised without the network.
/// </summary>
public interface IPacketLink : IAsyncDisposable
{
    EndPoint? LocalEndPoint { get; }
    EndPoint? RemoteEndPoint { get; }

    /// <summary>Sends one datagram to the peer (best-effort; may be dropped, reordered, or duplicated).</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default);

    /// <summary>Receives the next datagram from the peer, or null when the link is closed.</summary>
    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default);
}
