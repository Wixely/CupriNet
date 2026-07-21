using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using CupriNet.Codex;

namespace CupriNet.Vessel;

/// <summary>
/// Accepts inbound reliable-UDP sessions on a single socket, demultiplexing datagrams by source endpoint into a
/// per-peer <see cref="ArqStream"/>/<see cref="Vessel"/> — the UDP analogue of <see cref="VesselListener"/>. Used
/// by a node that is directly reachable on a UDP port; a hole-punched pair instead uses <c>NatTraversal</c> with a
/// dedicated per-path socket.
/// </summary>
public sealed class UdpVesselListener : IAsyncDisposable
{
    private const int MaxDatagram = 1500;
    private readonly Socket _socket;
    private readonly int _maxFrameSize;
    private readonly ConcurrentDictionary<EndPoint, DemuxLink> _peers = new();
    private readonly Channel<Vessel> _accepted = Channel.CreateUnbounded<Vessel>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveLoop;

    public UdpVesselListener(IPEndPoint endpoint, int maxFrameSize = FrameCodec.DefaultMaxFrameSize)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(endpoint);
        DisableUdpConnReset(_socket);
        _maxFrameSize = maxFrameSize;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>Returns the next inbound session (a new source endpoint's first datagram opens one).</summary>
    public async Task<Vessel> AcceptAsync(CancellationToken cancellationToken = default)
        => await _accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[MaxDatagram];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try { received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }

            var datagram = buffer[..received.ReceivedBytes];
            var link = _peers.GetOrAdd(received.RemoteEndPoint, OpenPeer);
            link.Deliver(datagram);
        }
    }

    private DemuxLink OpenPeer(EndPoint remote)
    {
        var link = new DemuxLink(_socket, remote, LocalEndPoint);
        var stream = new ArqStream(link);
        var vessel = new Vessel(stream, LocalEndPoint, remote, _maxFrameSize,
            () => { _peers.TryRemove(remote, out _); return ValueTask.CompletedTask; });
        _accepted.Writer.TryWrite(vessel);
        return link;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();
        _accepted.Writer.TryComplete();
        try { await _receiveLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }

    private static void DisableUdpConnReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
            return;
        const int SioUdpConnReset = -1744830452;
        socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
    }

    /// <summary>A per-peer view of the shared listener socket: inbound datagrams are pushed in by the demux loop;
    /// outbound go to this peer's endpoint. It never closes the shared socket.</summary>
    private sealed class DemuxLink(Socket socket, EndPoint remote, EndPoint? local) : IPacketLink
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();

        public EndPoint? LocalEndPoint => local;
        public EndPoint? RemoteEndPoint => remote;

        public void Deliver(byte[] datagram) => _inbound.Writer.TryWrite(datagram);

        public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
        {
            try { await socket.SendToAsync(datagram, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false); }
            catch (SocketException) { /* transient — ARQ retransmits */ }
            catch (ObjectDisposedException) { /* listener torn down */ }
        }

        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            try { return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
            catch (ChannelClosedException) { return null; }
        }

        public ValueTask DisposeAsync()
        {
            _inbound.Writer.TryComplete(); // do not close the shared listener socket
            return ValueTask.CompletedTask;
        }
    }
}
