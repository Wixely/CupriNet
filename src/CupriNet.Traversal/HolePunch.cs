using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace CupriNet.Traversal;

/// <summary>The outcome of a successful hole punch: the peer's confirmed, reachable UDP endpoint.</summary>
public sealed record HolePunchResult(IPEndPoint RemoteEndpoint);

/// <summary>
/// UDP hole punching via simultaneous connectivity checks (an ICE-style exchange). Two peers that have learned
/// each other's candidate endpoints (via a rendezvous / Ferryman) both send PROBEs toward the other and reply
/// with ACKs; the outbound PROBEs open each side's NAT mapping so the peer's packets get through. A path is
/// confirmed once a peer has both seen the other's PROBE and had its own PROBE acknowledged. Probes are matched
/// by a shared session id agreed out-of-band, so stray packets are ignored.
/// <para>
/// The punch runs over a raw <see cref="Socket"/> whose NAT mapping stays warm; on success the caller can take
/// that same socket (<see cref="TakeSocket"/>) and carry the reliable-UDP session over it — the mapping the
/// punch opened. Probe/ack markers are chosen so they never collide with the reliable-UDP wire, so a lingering
/// peer's late probes can share the socket harmlessly. After confirming, the receiver lingers briefly, still
/// answering the peer's probes, so the peer confirms too before the socket is handed to the data path.
/// </para>
/// </summary>
public sealed class HolePunch : IDisposable
{
    // Markers chosen outside the reliable-UDP command range {1,2,3} so a stray probe on a shared data socket
    // is ignored by ReliableArq (and vice versa).
    private const byte Probe = 0xF1;
    private const byte Ack = 0xF2;
    private const int SessionIdSize = 16;
    private const int NonceSize = 16;
    private const int MessageSize = 1 + SessionIdSize + NonceSize;

    private readonly byte[] _sessionId;
    private readonly Socket _socket;
    private readonly bool _ownsSocket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _detached;

    public HolePunch(byte[] sessionId, IPEndPoint bindEndpoint)
        : this(sessionId, CreateBound(bindEndpoint), ownsSocket: true) { }

    public HolePunch(byte[] sessionId, Socket socket, bool ownsSocket)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(socket);
        if (sessionId.Length != SessionIdSize)
            throw new ArgumentException($"Session id must be {SessionIdSize} bytes.", nameof(sessionId));

        _sessionId = (byte[])sessionId.Clone();
        _socket = socket;
        _ownsSocket = ownsSocket;
        DisableUdpConnReset(_socket);
    }

    private static Socket CreateBound(IPEndPoint bindEndpoint)
    {
        ArgumentNullException.ThrowIfNull(bindEndpoint);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(bindEndpoint);
        return socket;
    }

    /// <summary>The bound local endpoint (its candidate; reflexive candidates come from the reflexive exchange).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>
    /// Hands the warm socket to the caller and stops this instance from closing it, so the reliable-UDP session
    /// can be carried over the very mapping the punch opened. Call only after <see cref="PunchAsync"/> succeeds.
    /// </summary>
    public Socket TakeSocket()
    {
        _detached = true;
        return _socket;
    }

    /// <summary>Runs connectivity checks against the peer's candidate endpoints until a path is confirmed or it times out.</summary>
    public async Task<HolePunchResult> PunchAsync(
        IReadOnlyList<IPEndPoint> peerCandidates, TimeSpan interval, TimeSpan timeout,
        CancellationToken cancellationToken = default, TimeSpan? linger = null)
    {
        ArgumentNullException.ThrowIfNull(peerCandidates);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        var myNonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tcs = new TaskCompletionSource<HolePunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Only ever interact with an endpoint the peer actually advertised out-of-band. A forged probe or ack from
        // any other source — an off-path or on-path observer that learned the (public) session id — is ignored, so
        // it cannot redirect the confirmed path (and thus the data socket) to an attacker-chosen address. The punch
        // itself stays unauthenticated by design; the subsequent Noise handshake authenticates the peer.
        var allowedAddresses = peerCandidates.Select(c => c.Address).ToHashSet();

        var receiver = ReceiveLoopAsync(myNonce, allowedAddresses, tcs, cts.Token);
        var sender = SendLoopAsync(peerCandidates, myNonce, interval, sendCts.Token);

        try
        {
            var result = await tcs.Task.ConfigureAwait(false);
            // Confirmed: stop our own probes, but keep answering the peer's for a linger so it confirms too
            // (its probes then land harmlessly on the data socket, ignored by the reliable-UDP layer).
            await sendCts.CancelAsync().ConfigureAwait(false);
            try { await Task.Delay(linger ?? TimeSpan.FromMilliseconds(400), cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return result;
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(Swallow(receiver), Swallow(sender)).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(byte[] myNonce, IReadOnlySet<IPAddress> allowedAddresses, TaskCompletionSource<HolePunchResult> tcs, CancellationToken cancellationToken)
    {
        var sawPeerProbe = false;
        var ourProbeAcked = false;
        IPEndPoint? confirmed = null;
        var buffer = new byte[MessageSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try
                {
                    received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, AnyEndpoint, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    continue; // transient (e.g. an unreachable candidate) — keep probing
                }

                if (received.ReceivedBytes != MessageSize)
                    continue;
                if (!buffer.AsSpan(1, SessionIdSize).SequenceEqual(_sessionId))
                    continue;

                var remote = (IPEndPoint)received.RemoteEndPoint;
                if (!allowedAddresses.Contains(remote.Address))
                    continue; // not one of the peer's advertised addresses — a forged/misdirected packet; ignore it

                var nonce = buffer.AsSpan(1 + SessionIdSize, NonceSize).ToArray();
                switch (buffer[0])
                {
                    case Probe:
                        await SendAsync(BuildMessage(Ack, nonce), remote, cancellationToken).ConfigureAwait(false);
                        sawPeerProbe = true;
                        break;
                    case Ack when nonce.AsSpan().SequenceEqual(myNonce):
                        ourProbeAcked = true;
                        confirmed = remote;
                        break;
                }

                if (sawPeerProbe && ourProbeAcked && confirmed is not null)
                    tcs.TrySetResult(new HolePunchResult(confirmed)); // but keep looping to answer the peer's probes
            }
        }
        catch (OperationCanceledException) { }

        tcs.TrySetException(new TimeoutException("Hole punch did not confirm a path before timing out."));
    }

    private async Task SendLoopAsync(IReadOnlyList<IPEndPoint> peerCandidates, byte[] myNonce, TimeSpan interval, CancellationToken cancellationToken)
    {
        var probe = BuildMessage(Probe, myNonce);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var candidate in peerCandidates)
                    await SendAsync(probe, candidate, cancellationToken).ConfigureAwait(false);
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendAsync(byte[] datagram, IPEndPoint target, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _socket.SendToAsync(datagram, SocketFlags.None, target, cancellationToken).ConfigureAwait(false); }
        catch (SocketException) { /* transient */ }
        finally { _sendLock.Release(); }
    }

    private byte[] BuildMessage(byte type, ReadOnlySpan<byte> nonce)
    {
        var message = new byte[MessageSize];
        message[0] = type;
        _sessionId.CopyTo(message.AsSpan(1));
        nonce.CopyTo(message.AsSpan(1 + SessionIdSize));
        return message;
    }

    private static readonly EndPoint AnyEndpoint = new IPEndPoint(IPAddress.Any, 0);

    private static async Task Swallow(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { /* teardown */ }
    }

    private static void DisableUdpConnReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
            return;
        // Stop Windows surfacing ICMP port-unreachable as a socket exception on the next receive.
        const int SioUdpConnReset = -1744830452;
        socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
    }

    public void Dispose()
    {
        if (_ownsSocket && !_detached)
            _socket.Dispose();
        _sendLock.Dispose();
    }
}
