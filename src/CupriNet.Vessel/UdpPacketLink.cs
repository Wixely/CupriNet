using System.Net;
using System.Net.Sockets;

namespace CupriNet.Vessel;

/// <summary>
/// An <see cref="IPacketLink"/> over a connected UDP socket (1:1 with one peer). Connecting fixes the default
/// remote, so the socket only receives datagrams from that peer — the substrate for a single reliable session
/// over a hole-punched path. A full many-peer UDP listener (demultiplexing by source endpoint) is a later step.
/// </summary>
public sealed class UdpPacketLink : IPacketLink
{
    private const int MaxDatagram = 1500;
    private readonly Socket _socket;
    private readonly EndPoint _remote;

    public UdpPacketLink(Socket socket, EndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(remote);
        _socket = socket;
        _remote = remote;
    }

    /// <summary>Binds a UDP socket to <paramref name="localEndpoint"/> and connects it to <paramref name="remote"/>.</summary>
    public static UdpPacketLink Bind(IPEndPoint localEndpoint, IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);
        ArgumentNullException.ThrowIfNull(remote);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(localEndpoint);
        socket.Connect(remote); // fixes the default peer; receives only from it
        return new UdpPacketLink(socket, remote);
    }

    public EndPoint? LocalEndPoint => _socket.LocalEndPoint;
    public EndPoint? RemoteEndPoint => _remote;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
    {
        try { await _socket.SendAsync(datagram, SocketFlags.None, cancellationToken).ConfigureAwait(false); }
        catch (SocketException) { /* transient — the ARQ recovers via retransmission */ }
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var buffer = new byte[MaxDatagram];
        while (true)
        {
            try
            {
                var n = await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                return buffer[..n];
            }
            catch (OperationCanceledException) { return null; }
            catch (SocketException) { continue; } // e.g. ICMP port-unreachable — ignore and keep receiving
            catch (ObjectDisposedException) { return null; }
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
