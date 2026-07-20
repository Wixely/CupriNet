using System.Net;
using System.Security.Cryptography;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

public class EpistleSessionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static async Task<(VesselSession A, VesselSession B, VesselListener Listener)> ConnectedPairAsync(CancellationToken ct)
    {
        var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        var a = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        var b = await acceptTask;
        return (a, b, listener);
    }

    [Fact]
    public async Task EndToEnd_Message_IsDelivered_AndAcked_OverEncryptedSession()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();

        // Both members share a Consecration Veil session key (simulated here directly).
        var sessionKey = RandomNumberGenerator.GetBytes(suite.Aead.KeySize);

        var (va, vb, listener) = await ConnectedPairAsync(ct);
        await using var _a = va;
        await using var _b = vb;
        await using var _l = listener;

        var alice = new EpistleSession(va, sessionKey, suite);
        var bob = new EpistleSession(vb, sessionKey, suite);

        var deduper = new EpistleDeduper();
        var vigil = new Vigil();

        // Alice sends, tracked by her Vigil.
        var epistle = Epistle.Text("Hello, Arcanum!", Now);
        vigil.Enqueue(epistle, Now);
        foreach (var due in vigil.CollectDue(Now).ToSend)
            await alice.SendMessageAsync(due, ct);

        // Bob receives, dedups, delivers, and acks.
        var received = await bob.ReceiveAsync(ct);
        var message = Assert.IsType<MessageReceived>(received);
        Assert.Equal("Hello, Arcanum!", message.Epistle.AsText());
        Assert.True(deduper.TryMarkSeen(message.Epistle.MessageId));
        await bob.SendAttestationAsync(message.Epistle.MessageId, ct);

        // Alice receives the ack and her Vigil clears.
        var ack = await alice.ReceiveAsync(ct);
        var attestation = Assert.IsType<AttestationReceived>(ack);
        Assert.True(vigil.Acknowledge(attestation.MessageId));
        Assert.Equal(0, vigil.PendingCount);
    }

    [Fact]
    public async Task Redelivery_IsIdempotent_ReceiverDeliversOnce()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();
        var sessionKey = RandomNumberGenerator.GetBytes(suite.Aead.KeySize);

        var (va, vb, listener) = await ConnectedPairAsync(ct);
        await using var _a = va;
        await using var _b = vb;
        await using var _l = listener;

        var alice = new EpistleSession(va, sessionKey, suite);
        var bob = new EpistleSession(vb, sessionKey, suite);
        var deduper = new EpistleDeduper();

        // Simulate a lost ack: Alice sends the same Epistle twice.
        var epistle = Epistle.Text("retry me", Now);
        await alice.SendMessageAsync(epistle, ct);
        await alice.SendMessageAsync(epistle, ct);

        var first = Assert.IsType<MessageReceived>(await bob.ReceiveAsync(ct));
        var second = Assert.IsType<MessageReceived>(await bob.ReceiveAsync(ct));

        Assert.True(deduper.TryMarkSeen(first.Epistle.MessageId));   // delivered
        Assert.False(deduper.TryMarkSeen(second.Epistle.MessageId)); // duplicate -> not delivered again
    }
}
