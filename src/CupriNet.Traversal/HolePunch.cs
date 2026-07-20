using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace CupriNet.Traversal;

/// <summary>The outcome of a successful hole punch: the peer's confirmed, reachable UDP endpoint.</summary>
public sealed record HolePunchResult(IPEndPoint RemoteEndpoint);

/// <summary>
/// UDP hole punching via simultaneous connectivity checks (an ICE-style exchange). Two peers that have
/// learned each other's candidate endpoints (via a rendezvous / Ferryman) both send PROBEs toward the
/// other and reply with ACKs; the outbound PROBEs open each side's NAT mapping so the peer's packets get
/// through. A path is confirmed once a peer has both seen the other's PROBE and had its own PROBE
/// acknowledged. Probes are matched by a shared session id agreed out-of-band, so stray packets are ignored.
/// </summary>
public sealed class HolePunch : IDisposable
{
    private const byte Probe = 1;
    private const byte Ack = 2;
    private const int SessionIdSize = 16;
    private const int NonceSize = 8;
    private const int MessageSize = 1 + SessionIdSize + NonceSize;

    private readonly byte[] _sessionId;
    private readonly UdpClient _udp;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public HolePunch(byte[] sessionId, IPEndPoint bindEndpoint)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(bindEndpoint);
        if (sessionId.Length != SessionIdSize)
            throw new ArgumentException($"Session id must be {SessionIdSize} bytes.", nameof(sessionId));

        _sessionId = (byte[])sessionId.Clone();
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.Bind(bindEndpoint);
        DisableUdpConnReset(_udp);
    }

    /// <summary>The bound local endpoint (its candidate; reflexive candidates come from the reflexive exchange).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    /// <summary>Runs connectivity checks against the peer's candidate endpoints until a path is confirmed or it times out.</summary>
    public async Task<HolePunchResult> PunchAsync(
        IReadOnlyList<IPEndPoint> peerCandidates, TimeSpan interval, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peerCandidates);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var myNonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tcs = new TaskCompletionSource<HolePunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var receiver = ReceiveLoopAsync(myNonce, tcs, cts.Token);
        var sender = SendLoopAsync(peerCandidates, myNonce, interval, cts.Token);

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(Swallow(receiver), Swallow(sender)).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(byte[] myNonce, TaskCompletionSource<HolePunchResult> tcs, CancellationToken cancellationToken)
    {
        var sawPeerProbe = false;
        var ourProbeAcked = false;
        IPEndPoint? confirmed = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    continue; // transient (e.g. an unreachable candidate) — keep probing
                }

                var buffer = result.Buffer;
                if (buffer.Length != MessageSize)
                    continue;
                if (!buffer.AsSpan(1, SessionIdSize).SequenceEqual(_sessionId))
                    continue;

                var nonce = buffer.AsSpan(1 + SessionIdSize, NonceSize);
                switch (buffer[0])
                {
                    case Probe:
                        await SendAsync(BuildMessage(Ack, nonce), result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                        sawPeerProbe = true;
                        break;
                    case Ack when nonce.SequenceEqual(myNonce):
                        ourProbeAcked = true;
                        confirmed = result.RemoteEndPoint;
                        break;
                }

                if (sawPeerProbe && ourProbeAcked && confirmed is not null)
                {
                    tcs.TrySetResult(new HolePunchResult(confirmed));
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // timed out or cancelled
        }

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
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendAsync(byte[] datagram, IPEndPoint target, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _udp.SendAsync(datagram, target, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private byte[] BuildMessage(byte type, ReadOnlySpan<byte> nonce)
    {
        var message = new byte[MessageSize];
        message[0] = type;
        _sessionId.CopyTo(message.AsSpan(1));
        nonce.CopyTo(message.AsSpan(1 + SessionIdSize));
        return message;
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // teardown
        }
    }

    private static void DisableUdpConnReset(UdpClient udp)
    {
        if (!OperatingSystem.IsWindows())
            return;
        // Stop Windows surfacing ICMP port-unreachable as a socket exception on the next receive.
        const int SioUdpConnReset = -1744830452;
        udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
    }

    public void Dispose()
    {
        _udp.Dispose();
        _sendLock.Dispose();
    }
}
