using CupriNet.Core;
using CupriNet.Vessel;

namespace CupriNet.Hosting;

/// <summary>
/// WebRTC transport wiring. When an <see cref="IWebRtcTransport"/> is supplied, the node advertises its static
/// endpoint parameters in every Intonation (so a browser can dial it with no signalling server) and accepts inbound
/// browser DataChannels, completing each pairing through the transport-agnostic <see cref="AcceptChannelOverVesselAsync"/>
/// seam and surfacing the peer through the normal <see cref="AcceptAsync"/> queue. A browser client is thus just
/// another peer — the Toll, Noise handshake (which is the real authentication), mux, Consecration, and Rites all run
/// over the DataChannel unchanged. See <c>design/webrtc-clients.md</c>.
/// </summary>
public sealed partial class CupriNode
{
    private IWebRtcTransport? _webRtc;

    internal void StartWebRtc()
    {
        _webRtc = _options.WebRtcTransport;
        if (_webRtc is null)
            return;
        _webRtc.ChannelAccepted += OnWebRtcVessel;
        _ = StartWebRtcAsync(_lifetime.Token);
    }

    /// <summary>The WebRTC endpoint to stamp into the Intonation: a live transport's parameters take precedence over
    /// any static <see cref="CupriNodeOptions.WebRtc"/> value; null when neither is present.</summary>
    internal WebRtcEndpoint? WebRtcEndpointToAdvertise => _webRtc?.Parameters ?? _options.WebRtc;

    private async Task StartWebRtcAsync(CancellationToken cancellationToken)
    {
        try { await _webRtc!.StartAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { /* best-effort — WebRTC unavailable; the node still runs on other transports */ }
    }

    // A browser opened a DataChannel. Complete the inbound pairing over it and surface the peer, off the transport's
    // callback thread. On failure the seam has already disposed the vessel.
    private void OnWebRtcVessel(IVessel vessel) => _ = AcceptWebRtcAsync(vessel, _lifetime.Token);

    private async Task AcceptWebRtcAsync(IVessel vessel, CancellationToken cancellationToken)
    {
        try
        {
            var peer = await AcceptChannelOverVesselAsync(vessel, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            if (!_accepted.Writer.TryWrite(peer))
                await peer.DisposeAsync().ConfigureAwait(false); // no one is accepting (or the backlog is full) — drop it
        }
        catch { /* handshake failed / not a channel session — the vessel is already disposed */ }
    }

    private async ValueTask DisposeWebRtcAsync()
    {
        if (_webRtc is not null)
        {
            try { await _webRtc.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
    }
}
