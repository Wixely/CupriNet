using System.Net;
using System.Threading.Channels;
using CupriNet.Vessel;
using CupriWebRTC;
using CupriWebRTC.Sctp;

namespace CupriNet.WebRtc;

/// <summary>
/// Adapts one CupriWebRTC <see cref="WebRtcChannel"/> (a single peer's DataChannel) to CupriNet's message-oriented
/// <see cref="IDataChannel"/>. Inbound application messages are queued and drained by <see cref="ReceiveAsync"/>;
/// <see cref="SendAsync"/> writes them back as binary WebRTC data. A <see cref="DataChannelVessel"/> over this then
/// drops straight into <c>AcceptChannelOverVesselAsync</c>, so Noise / Consecration / Rites run over the browser's
/// channel unchanged. Each peer has its own channel, so many browsers never cross-talk.
/// </summary>
internal sealed class WebRtcDataChannel : IDataChannel
{
    private readonly WebRtcChannel _channel;
    private readonly Channel<byte[]> _inbound =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    public WebRtcDataChannel(WebRtcChannel channel, EndPoint? localEndPoint)
    {
        _channel = channel;
        LocalEndPoint = localEndPoint;
        // Subscribe synchronously (on the ChannelOpened callback) so no message for this channel is missed.
        _channel.MessageReceived += (_, data) => _inbound.Writer.TryWrite(data);
        _channel.Closed += () => _inbound.Writer.TryComplete();
    }

    /// <summary>
    /// 0 — unknown — because CupriWebRTC does not surface it yet. The negotiated value exists on the SCTP
    /// association (<c>SctpAssociation.MaxMessageBytes</c>), but <see cref="WebRtcChannel"/> does not expose it, so
    /// this adapter has no way to read what the two ends actually agreed to.
    ///
    /// <para>Reporting 0 means <see cref="RiteTransport.EnsureCarriesRiteFrames"/> passes rather than guessing, which
    /// is the right failure while the number is unavailable: this stack offers 256 KiB and every pairing measured so
    /// far agrees to it, so refusing on a value we cannot see would break working connections. The moment CupriWebRTC
    /// exposes the association's figure on the channel, return it here and the check starts doing its job — that is
    /// the whole of what is left to make a low-negotiating peer a clear connect-time error instead of a frame the
    /// wire silently refuses.</para>
    /// </summary>
    public int MaxMessageBytes => 0;

    public EndPoint? RemoteEndPoint => _channel.Remote;
    public EndPoint? LocalEndPoint { get; }

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        // CupriNet frames always carry at least a varuint stream-id prefix, so a message is never empty (which SCTP
        // DATA can't be) — binary PPID is always correct here.
        _channel.Send(Dcep.PpidBinary, message.Span);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                if (_inbound.Reader.TryRead(out var message))
                    return message;
            return null; // channel closed
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
