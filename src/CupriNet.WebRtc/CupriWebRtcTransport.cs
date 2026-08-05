using System.Net;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Vessel;
using CupriWebRTC;
using CupriWebRTC.Ice;

namespace CupriNet.WebRtc;

/// <summary>
/// A concrete <see cref="IWebRtcTransport"/> backed by the CupriWebRTC package: binds a UDP socket and runs the
/// ICE-lite / DTLS / SCTP stack so browsers can open WebRTC DataChannels to the node with no signalling server
/// (the node's signed Intonation carries the static <see cref="Parameters"/> the browser dials). Each opened channel
/// is wrapped as an <see cref="IVessel"/> and raised on <see cref="ChannelAccepted"/>; the node then runs its ordinary
/// Toll + Noise + Consecration stack over it. Many browsers are served on the one socket (demultiplexed by source
/// address), each an independent peer. Supply it via <see cref="CupriNodeOptions.WebRtcTransport"/>.
/// </summary>
public sealed class CupriWebRtcTransport : IWebRtcTransport
{
    private readonly WebRtcListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _linked;
    private Task? _run;

    /// <summary>Binds a WebRTC listener on <paramref name="bind"/> (use port 0 to let the OS choose). ICE credentials
    /// are generated fresh; an Intonation is a short-lived snapshot, so stable creds across restarts aren't needed.
    /// <paramref name="maxSessions"/> caps concurrent browser peers (a Ward against a public endpoint being swamped);
    /// <paramref name="sessionIdleTimeout"/> evicts a peer that has gone silent (null = the library default).</summary>
    public CupriWebRtcTransport(IPEndPoint bind, int maxSessions = WebRtcListener.DefaultMaxSessions, TimeSpan? sessionIdleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(bind);
        _listener = new WebRtcListener(bind, IceCredentials.Generate(), maxSessions: maxSessions, sessionIdleTimeout: sessionIdleTimeout);
        _listener.ChannelOpened += OnChannelOpened;
    }

    /// <inheritdoc/>
    public event Action<IVessel>? ChannelAccepted;

    /// <inheritdoc/>
    public WebRtcEndpoint Parameters
    {
        get
        {
            var p = _listener.Parameters;
            return new WebRtcEndpoint
            {
                IceUfrag = p.IceUfrag,
                IcePassword = p.IcePassword,
                FingerprintAlgorithm = p.FingerprintAlgorithm,
                Fingerprint = p.Fingerprint,
                Port = p.Port,
            };
        }
    }

    /// <summary>The bound local endpoint (reflects the OS-assigned port when 0 was requested).</summary>
    public IPEndPoint LocalEndPoint => _listener.LocalEndPoint;

    /// <summary>The number of live browser sessions.</summary>
    public int SessionCount => _listener.SessionCount;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // RunAsync is the ICE receive loop; it runs until the transport is disposed or the caller cancels.
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _run = _listener.RunAsync(_linked.Token);
        return Task.CompletedTask;
    }

    // Each browser's DataChannel becomes its own vessel; the node pairs and surfaces it as an independent peer.
    private void OnChannelOpened(WebRtcChannel channel)
        => ChannelAccepted?.Invoke(new DataChannelVessel(new WebRtcDataChannel(channel, _listener.LocalEndPoint)));

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _listener.DisposeAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            try { await _run.ConfigureAwait(false); }
            catch { /* cancellation / shutdown */ }
        }
        _linked?.Dispose();
        _cts.Dispose();
    }
}
