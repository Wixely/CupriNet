using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CupriNet.Abstractions;
using CupriNet.Core;
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
    private const int MaxDiscoveredPeers = 512;

    private LanDiscovery? _lan;
    private readonly ConcurrentDictionary<Sigil, DiscoveredNode> _discoveredPeers = new();

    /// <summary>Raised when a same-network peer's signed presence is discovered. Handlers should be quick.</summary>
    public event Action<DiscoveredNode>? LanPeerDiscovered;

    /// <summary>A snapshot of peers discovered on the LAN so far.</summary>
    public IReadOnlyCollection<DiscoveredNode> DiscoveredPeers => _discoveredPeers.Values.ToList();

    internal void StartLanDiscovery()
    {
        if (!_options.EnableLanDiscovery || _options.Mode == ReachabilityMode.TorOnly)
            return; // Tor-only: no LAN broadcast (it would announce our Sigil + local address)

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
            // Ward: an attacker can mint unlimited keypairs and sign a valid presence for each. Cap the table so a
            // flood of fresh Sigils can't grow it without bound; updates to known peers always apply.
            if (isNew && _discoveredPeers.Count >= MaxDiscoveredPeers)
                continue;
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

    // ---- Mutual-link hole punch (two-cone-NAT genesis, no rendezvous) --------------------------

    /// <summary>
    /// Pairs with a peer by hole punching, when both sides exchanged candidates out-of-band (each other's links)
    /// and start at roughly the same time — the two-restricted-NAT genesis case that needs no online rendezvous,
    /// because the out-of-band link exchange <em>is</em> the signaling. Both derive the same session id from the
    /// two Sigils and punch toward each other's <paramref name="peerCandidates"/>; the lower Sigil then initiates
    /// the Noise handshake and the higher accepts. The caller binds <paramref name="boundPunchSocket"/> and puts
    /// its endpoint (host + reflexive) in its own link so the peer can punch back.
    /// </summary>
    public async Task<PairedPeer> ConjoinViaMutualPunchAsync(
        Socket boundPunchSocket, IReadOnlyList<IPEndPoint> peerCandidates, Sigil peerSigil, DateTimeOffset now,
        TimeSpan? window = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(boundPunchSocket);
        ArgumentNullException.ThrowIfNull(peerCandidates);

        // Subnet fence: only punch toward candidate addresses the policy permits.
        var candidates = peerCandidates.Where(c => IsAddressAllowed(c.Address)).ToList();
        if (candidates.Count == 0)
        {
            boundPunchSocket.Dispose();
            throw new CupriNodeException("No hole-punch candidate is permitted by this node's subnet policy.");
        }

        var sessionId = MutualPunchSessionId(Identity.Sigil, peerSigil);
        var timeout = window ?? TimeSpan.FromSeconds(30);

        CupriNet.Vessel.Vessel vessel;
        try
        {
            vessel = await NatTraversal.PunchAndConnectAsync(
                sessionId, boundPunchSocket, candidates, TimeSpan.FromMilliseconds(50), timeout, cancellationToken).ConfigureAwait(false);
        }
        catch { boundPunchSocket.Dispose(); throw; }

        // Deterministic role from the two Sigils: lower initiates, higher accepts.
        return SigilCompare(Identity.Sigil, peerSigil) < 0
            ? await ConjoinOverVesselAsync(vessel, peerSigil, now, cancellationToken).ConfigureAwait(false)
            : await AcceptChannelOverVesselAsync(vessel, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An order-independent 16-byte session id both peers derive identically from their two Sigils.</summary>
    private static byte[] MutualPunchSessionId(Sigil a, Sigil b)
    {
        var (lo, hi) = SigilCompare(a, b) <= 0 ? (a, b) : (b, a);
        var buffer = new byte[lo.Span.Length + hi.Span.Length];
        lo.Span.CopyTo(buffer);
        hi.Span.CopyTo(buffer.AsSpan(lo.Span.Length));
        return SHA256.HashData(buffer)[..16];
    }

    // ---- Automatic port mapping (NAT-PMP) ------------------------------------------------------

    private volatile Beacon? _mappedBeacon;

    /// <summary>The external Mapped beacon obtained via NAT-PMP, if the gateway forwarded our port; else null.</summary>
    public Beacon? PortMappedBeacon => _mappedBeacon;

    internal void StartPortMapping()
    {
        if (_options.EnablePortMapping && _options.Mode != ReachabilityMode.TorOnly) // no clearnet port to map in Tor-only
            _ = PortMappingLoopAsync(_lifetime.Token);
    }

    private async Task PortMappingLoopAsync(CancellationToken cancellationToken)
    {
        var lifetime = TimeSpan.FromSeconds(Math.Max(120, _options.PortMappingLifetimeSeconds));
        while (!cancellationToken.IsCancellationRequested)
        {
            try { _mappedBeacon = await PortMapper.TryMapTcpAsync(LocalEndPoint.Port, lifetime, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { _mappedBeacon = null; }

            // Renew at half the mapping's lifetime; retry sooner if it isn't established yet.
            var wait = _mappedBeacon is null ? TimeSpan.FromMinutes(1) : lifetime / 2;
            try { await Task.Delay(wait, cancellationToken).ConfigureAwait(false); }
            catch { break; }
        }
    }
}
