using System.Collections.Concurrent;
using System.Net;
using CupriNet.Abstractions;
using CupriNet.Traversal;

namespace CupriNet.Hosting;

/// <summary>
/// LAN discovery: announce this node's presence on the local network and discover same-network peers, so two
/// nodes on one LAN can pair with no link and no NAT — the easiest genesis path. Discovered peers surface via
/// <see cref="LanPeerDiscovered"/>; pairing with one goes through the transport-agnostic
/// <see cref="ConjoinOverVesselAsync"/> seam pinned to the discovered (signed) Sigil, so no Intonation is needed.
/// </summary>
public sealed partial class CupriNode
{
    private LanDiscovery? _lan;
    private readonly ConcurrentDictionary<Sigil, DiscoveredNode> _discoveredPeers = new();

    /// <summary>Raised when a same-network peer's signed presence is discovered. Handlers should be quick.</summary>
    public event Action<DiscoveredNode>? LanPeerDiscovered;

    /// <summary>A snapshot of peers discovered on the LAN so far.</summary>
    public IReadOnlyCollection<DiscoveredNode> DiscoveredPeers => _discoveredPeers.Values.ToList();

    internal void StartLanDiscovery()
    {
        if (!_options.EnableLanDiscovery)
            return;

        var targets = _options.LanAnnounceTargets ?? [new IPEndPoint(IPAddress.Broadcast, _options.LanDiscoveryPort)];
        var bind = new IPEndPoint(IPAddress.Any, _options.LanDiscoveryPort);
        try { _lan = new LanDiscovery(Identity, Network, Suite, bind, targets); }
        catch { return; } // discovery is best-effort — a bind conflict must never fail node startup

        _ = LanAnnounceLoopAsync(_lan, _lifetime.Token);
        _ = LanReceiveLoopAsync(_lan, _lifetime.Token);
    }

    private async Task LanAnnounceLoopAsync(LanDiscovery lan, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.LanAnnounceIntervalSeconds));
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await lan.AnnounceAsync(LocalEndPoint.Port, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { /* transient send failure — keep announcing */ }
            try { await Task.Delay(interval, cancellationToken).ConfigureAwait(false); }
            catch { break; }
        }
    }

    private async Task LanReceiveLoopAsync(LanDiscovery lan, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DiscoveredNode node;
            try { node = await lan.ReceiveAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { continue; }

            var isNew = !_discoveredPeers.ContainsKey(node.Sigil);
            _discoveredPeers[node.Sigil] = node;
            if (isNew)
            {
                try { LanPeerDiscovered?.Invoke(node); }
                catch { /* a misbehaving handler must not kill discovery */ }
            }
        }
    }

    /// <summary>
    /// Pairs with a LAN-discovered peer: dials its advertised host endpoint and completes the channel pairing over
    /// the seam, pinned to the discovered Sigil (which came from a signed presence). No Intonation required — the
    /// peer's ordinary <see cref="AcceptAsync"/> handles the responder side.
    /// </summary>
    public async Task<PairedPeer> ConjoinDiscoveredAsync(DiscoveredNode node, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        var vessel = await DialTcpAsync(node.ToBeacon(), cancellationToken).ConfigureAwait(false);
        return await ConjoinOverVesselAsync(vessel, node.Sigil, now, cancellationToken).ConfigureAwait(false);
    }

    private void DisposeLan() => _lan?.Dispose();
}
