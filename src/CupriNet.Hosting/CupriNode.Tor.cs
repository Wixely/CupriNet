using CupriNet.Abstractions;
using CupriNet.Core;

namespace CupriNet.Hosting;

/// <summary>
/// Onion (Tor) transport wiring. When an <see cref="IOnionTransport"/> is supplied, the node publishes an onion
/// service forwarding to its TCP listener (so inbound onion connections arrive through the ordinary
/// <see cref="AcceptAsync"/> path) and can dial peers' <c>.onion</c> addresses, completing the pairing through the
/// transport-agnostic <see cref="ConjoinOverVesselAsync"/> seam. Tor is an opt-in, slow, IP-hiding candidate lane;
/// cover traffic is deliberately never routed over it.
/// </summary>
public sealed partial class CupriNode
{
    /// <summary>The virtual port peers dial on an onion address (mapped to the node's local listener).</summary>
    public const int OnionVirtualPort = 43820;

    private IOnionTransport? _onion;
    private volatile Beacon? _onionBeacon;

    /// <summary>Our published <c>.onion</c> beacon once the onion service is up, else null.</summary>
    public Beacon? OnionBeacon => _onionBeacon;

    internal void StartTor()
    {
        _onion = _options.OnionTransport;
        if (_onion is not null)
            _ = StartTorAsync(_lifetime.Token);
    }

    private async Task StartTorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _onion!.StartAsync(cancellationToken).ConfigureAwait(false);
            var address = await _onion.PublishAsync(OnionVirtualPort, LocalEndPoint.Port, cancellationToken).ConfigureAwait(false);
            _onionBeacon = new Beacon(EndpointKind.Onion, address, OnionVirtualPort);
        }
        catch (OperationCanceledException) { }
        catch { /* best-effort — Tor unavailable/blocked; node still runs on other transports */ }
    }

    /// <summary>
    /// Pairs with a peer over Tor: dials its <c>.onion</c> and completes the channel pairing through the seam,
    /// pinned to the expected Sigil. This is the IP-hiding, slow-lane candidate. Requires a configured transport.
    /// </summary>
    public async Task<PairedPeer> ConjoinViaOnionAsync(string onionAddress, Sigil peerSigil, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (_onion is null)
            throw new CupriNodeException("No onion transport is configured (set CupriNodeOptions.OnionTransport).");
        var vessel = await _onion.ConnectAsync(onionAddress, OnionVirtualPort, cancellationToken).ConfigureAwait(false);
        return await ConjoinOverVesselAsync(vessel, peerSigil, now, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask DisposeTorAsync()
    {
        if (_onion is not null)
        {
            try { await _onion.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
    }
}
