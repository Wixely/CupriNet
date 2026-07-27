using CupriNet.Vessel;

namespace CupriNet.Hosting;

/// <summary>
/// The seam CupriNet uses to reach peers over Tor (or any onion transport) without depending on a specific Tor
/// implementation. An app supplies a concrete transport (e.g. one backed by the CupriTor package) via
/// <see cref="CupriNodeOptions.OnionTransport"/>; the node dials/publishes through it and runs the ordinary
/// Noise + mux + Consecration stack over the resulting <see cref="IVessel"/> via the transport-agnostic pairing
/// seams. Keeping this an interface means <c>CupriNet.Hosting</c> never references the Tor library directly.
/// </summary>
public interface IOnionTransport : IAsyncDisposable
{
    /// <summary>Human-readable progress during the (slow) bootstrap/connect, e.g. "[45%] Fetching consensus".</summary>
    event Action<string>? Status;

    /// <summary>Bootstraps the transport (e.g. connect to the Tor network). Slow; watch <see cref="Status"/> for progress.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Dials a peer's <c>.onion</c> at <paramref name="virtualPort"/> and returns a connected vessel.</summary>
    Task<IVessel> ConnectAsync(string onionAddress, int virtualPort, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an onion service that forwards <paramref name="virtualPort"/> to <c>127.0.0.1:localPort</c>
    /// (this node's listener), and returns our own <c>.onion</c> address to advertise as an Onion beacon.
    /// </summary>
    Task<string> PublishAsync(int virtualPort, int localPort, CancellationToken cancellationToken = default);
}
