using System.Net;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Vessel;
using CupriWebRTC;
using CupriWebRTC.Ice;
using CupriWebRTC.Sctp;

namespace CupriNet.WebRtc;

/// <summary>
/// A concrete <see cref="IWebRtcTransport"/> backed by the CupriWebRTC package: binds a UDP socket and runs the
/// ICE-lite / DTLS / SCTP stack so a browser can open a WebRTC DataChannel to the node with no signalling server
/// (the node's signed Intonation carries the static <see cref="Parameters"/> the browser dials). When a channel
/// opens, it is wrapped as an <see cref="IVessel"/> and raised on <see cref="ChannelAccepted"/>; the node then runs
/// its ordinary Toll + Noise + Consecration stack over it. Supply it via <see cref="CupriNodeOptions.WebRtcTransport"/>.
///
/// <para><b>Current limitation:</b> the underlying <see cref="WebRtcListener"/> serves a single peer at a time, so
/// this accepts one browser DataChannel per listener. Run multiple listeners (or await a future multi-client
/// CupriWebRTC release) for concurrent browsers.</para>
/// </summary>
public sealed class CupriWebRtcTransport : IWebRtcTransport
{
    private readonly WebRtcListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private WebRtcDataChannel? _channel;
    private readonly object _gate = new();
    private CancellationTokenSource? _linked;
    private Task? _run;

    /// <summary>Binds a WebRTC listener on <paramref name="bind"/> (use port 0 to let the OS choose). ICE credentials
    /// are generated fresh; an Intonation is a short-lived snapshot, so stable creds across restarts aren't needed.</summary>
    public CupriWebRtcTransport(IPEndPoint bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        _listener = new WebRtcListener(bind, IceCredentials.Generate());
        _listener.MessageReceived += OnMessage;
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

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // RunAsync is the ICE receive loop; it runs until the transport is disposed or the caller cancels.
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _run = _listener.RunAsync(_linked.Token);
        return Task.CompletedTask;
    }

    private void OnChannelOpened(SctpDataChannel channel)
    {
        WebRtcDataChannel dc;
        lock (_gate)
        {
            _channel?.Complete();
            dc = new WebRtcDataChannel(_listener, channel.StreamId, _listener.LocalEndPoint);
            _channel = dc;
        }
        ChannelAccepted?.Invoke(new DataChannelVessel(dc));
    }

    private void OnMessage(ushort streamId, uint ppid, byte[] data)
    {
        WebRtcDataChannel? dc;
        lock (_gate)
            dc = _channel;
        if (dc is not null && streamId == dc.StreamId)
            dc.Deliver(data);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        lock (_gate)
            _channel?.Complete();
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
