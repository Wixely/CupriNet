using System.Net;
using System.Threading.Channels;
using CupriNet.Vessel;
using CupriWebRTC;
using CupriWebRTC.Sctp;

namespace CupriNet.WebRtc;

/// <summary>
/// Adapts one CupriWebRTC SCTP DataChannel (identified by its stream id on a <see cref="WebRtcListener"/>) to
/// CupriNet's message-oriented <see cref="IDataChannel"/>. Inbound application messages for the stream are queued and
/// drained by <see cref="ReceiveAsync"/>; <see cref="SendAsync"/> writes them back as binary WebRTC data. A
/// <see cref="DataChannelVessel"/> over this then drops straight into <c>AcceptChannelOverVesselAsync</c>, so Noise /
/// Consecration / Rites run over the browser's channel unchanged.
/// </summary>
internal sealed class WebRtcDataChannel : IDataChannel
{
    private readonly WebRtcListener _listener;
    private readonly Channel<byte[]> _inbound =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    public WebRtcDataChannel(WebRtcListener listener, ushort streamId, EndPoint? localEndPoint)
    {
        _listener = listener;
        StreamId = streamId;
        LocalEndPoint = localEndPoint;
    }

    /// <summary>The SCTP stream this channel occupies. CupriNet muxes its logical streams inside it.</summary>
    public ushort StreamId { get; }

    public EndPoint? RemoteEndPoint => null; // the listener does not surface the selected peer address (yet)
    public EndPoint? LocalEndPoint { get; }

    /// <summary>Hands an inbound application message for this stream to the receive queue (called by the listener).</summary>
    internal void Deliver(byte[] message) => _inbound.Writer.TryWrite(message);

    /// <summary>Marks the channel closed so a pending/next <see cref="ReceiveAsync"/> returns null.</summary>
    internal void Complete() => _inbound.Writer.TryComplete();

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        // CupriNet frames always carry at least a varuint stream-id prefix, so a message is never empty (which SCTP
        // DATA can't be) — binary PPID is always correct here.
        _listener.SendMessage(StreamId, Dcep.PpidBinary, message.ToArray());
        return ValueTask.CompletedTask;
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                if (_inbound.Reader.TryRead(out var message))
                    return message;
            return null; // channel completed (closed)
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
