using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CupriNet.Traversal;
using Xunit;

namespace CupriNet.UnitTests;

public class HolePunchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static IPEndPoint Loopback(int port) => new(IPAddress.Loopback, port);

    [Fact]
    public async Task TwoPeers_PunchThrough_AndConfirmEachOther()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        var sessionId = RandomNumberGenerator.GetBytes(16);
        using var alice = new HolePunch(sessionId, Loopback(0));
        using var bob = new HolePunch(sessionId, Loopback(0));

        var aliceEndpoint = alice.LocalEndPoint;
        var bobEndpoint = bob.LocalEndPoint;

        var interval = TimeSpan.FromMilliseconds(50);
        var punchTimeout = TimeSpan.FromSeconds(10);

        var aliceTask = alice.PunchAsync([bobEndpoint], interval, punchTimeout, ct);
        var bobTask = bob.PunchAsync([aliceEndpoint], interval, punchTimeout, ct);

        var aliceResult = await aliceTask;
        var bobResult = await bobTask;

        Assert.Equal(bobEndpoint.Port, aliceResult.RemoteEndpoint.Port);   // Alice confirmed Bob
        Assert.Equal(aliceEndpoint.Port, bobResult.RemoteEndpoint.Port);   // Bob confirmed Alice
    }

    [Fact]
    public async Task Punch_IgnoresPacketsFromANonAdvertisedAddress()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        var sessionId = RandomNumberGenerator.GetBytes(16);
        using var victim = new HolePunch(sessionId, Loopback(0));
        var victimEndpoint = victim.LocalEndPoint;

        // The peer's only advertised address is 127.0.0.2; the "attacker" below speaks from 127.0.0.1.
        var advertised = new IPEndPoint(IPAddress.Parse("127.0.0.2"), 40000);
        var punch = victim.PunchAsync([advertised], TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2), ct);

        // Forge a PROBE with the correct (public) session id from a non-advertised address.
        using var attacker = new UdpClient(Loopback(0));
        var forgedProbe = new byte[1 + 16 + 16];
        forgedProbe[0] = 0xF1;               // Probe marker
        sessionId.CopyTo(forgedProbe, 1);    // session id is not secret — the attacker knows it
        await attacker.SendAsync(forgedProbe, victimEndpoint, ct);

        // The victim must NOT answer a probe from 127.0.0.1 (it isn't the peer's advertised address), so no ack
        // comes back and the punch never confirms a path to the attacker.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await attacker.ReceiveAsync(idle.Token));

        await Assert.ThrowsAsync<TimeoutException>(async () => await punch);
    }

    [Fact]
    public async Task Punch_TimesOut_WhenNoPeerResponds()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        var sessionId = RandomNumberGenerator.GetBytes(16);
        using var lonely = new HolePunch(sessionId, Loopback(0));

        // Punch toward a port with nothing listening — no ACKs ever arrive.
        var deadPeer = Loopback(1); // reserved/unused on loopback
        await Assert.ThrowsAsync<TimeoutException>(
            async () => await lonely.PunchAsync([deadPeer], TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(600), ct));
    }

    [Fact]
    public async Task Punch_IgnoresWrongSession()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        using var alice = new HolePunch(RandomNumberGenerator.GetBytes(16), Loopback(0));
        using var stranger = new HolePunch(RandomNumberGenerator.GetBytes(16), Loopback(0)); // different session id

        // Alice probes the stranger; the stranger's probes/acks carry a different session id, so Alice
        // never confirms and times out.
        var strangerTask = stranger.PunchAsync([alice.LocalEndPoint], TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(600), ct);
        await Assert.ThrowsAsync<TimeoutException>(
            async () => await alice.PunchAsync([stranger.LocalEndPoint], TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(600), ct));
        try { await strangerTask; } catch { /* also times out */ }
    }
}
