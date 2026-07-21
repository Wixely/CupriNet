using System.Net;
using System.Net.Sockets;
using CupriNet.Codex;
using CupriNet.Vessel;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.Traversal;

/// <summary>
/// Bridges NAT hole punching to the reliable-UDP transport: given a bound socket and the peer's candidate
/// endpoints (learned out-of-band via a rendezvous / Ferryman), it punches a path and then carries a reliable,
/// ordered session over the very socket whose mapping the punch opened — yielding a <see cref="Vessel"/> that
/// Noise and the mux run over exactly as they would over TCP. This is the data-path half of NAT traversal: the
/// piece that turns a confirmed punch into an actual channel.
/// </summary>
public static class NatTraversal
{
    /// <summary>
    /// Punches toward <paramref name="peerCandidates"/> over <paramref name="boundSocket"/>, then returns a Vessel
    /// carrying reliable UDP over that same (now warm) socket. The caller binds the socket first and signals its
    /// local/reflexive candidates to the peer; both sides call this simultaneously. The returned Vessel owns the
    /// socket. Throws <see cref="TimeoutException"/> if no path confirms.
    /// </summary>
    public static async Task<VesselSession> PunchAndConnectAsync(
        byte[] sessionId,
        Socket boundSocket,
        IReadOnlyList<IPEndPoint> peerCandidates,
        TimeSpan interval,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        int maxFrameSize = FrameCodec.DefaultMaxFrameSize)
    {
        ArgumentNullException.ThrowIfNull(boundSocket);
        ArgumentNullException.ThrowIfNull(peerCandidates);

        HolePunchResult result;
        using (var punch = new HolePunch(sessionId, boundSocket, ownsSocket: false))
            result = await punch.PunchAsync(peerCandidates, interval, timeout, cancellationToken).ConfigureAwait(false);

        // Fix the default peer so the data socket only accepts the punched peer's datagrams, then run the session.
        boundSocket.Connect(result.RemoteEndpoint);
        return UdpVessel.Over(new UdpPacketLink(boundSocket, result.RemoteEndpoint), maxFrameSize);
    }

    /// <summary>Binds a fresh UDP socket to <paramref name="localEndpoint"/> for a traversal attempt (use its
    /// <see cref="Socket.LocalEndPoint"/> as a host candidate to signal to the peer before punching).</summary>
    public static Socket BindSocket(IPEndPoint localEndpoint)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(localEndpoint);
        return socket;
    }
}
