using System.Net;
using System.Security.Cryptography;
using CupriNet.Alembic;
using CupriNet.Codex;
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

        var alice = new EpistleSession(va, sessionKey, suite, VeilRole.Initiator);
        var bob = new EpistleSession(vb, sessionKey, suite, VeilRole.Responder);

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

        var alice = new EpistleSession(va, sessionKey, suite, VeilRole.Initiator);
        var bob = new EpistleSession(vb, sessionKey, suite, VeilRole.Responder);
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

    /// <summary>A stream channel that hands over exactly the payloads it is given, so a test can alter one in between.</summary>
    private sealed class Wire : IStreamChannel
    {
        public readonly Queue<byte[]> Sent = new();
        public readonly Queue<byte[]> Incoming = new();

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Sent.Enqueue(payload.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Incoming.TryDequeue(out var next) ? next : null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(4096)]
    public async Task AMessageId_IsSixteenBytes_AndNothingElseIsSentOrAccepted(int length)
    {
        var suite = CryptoSuites.Secure();
        var sessionKey = RandomNumberGenerator.GetBytes(suite.Aead.KeySize);
        var aliceWire = new Wire();
        var bobWire = new Wire();
        var alice = new EpistleSession(aliceWire, sessionKey, suite, VeilRole.Initiator);
        var bob = new EpistleSession(bobWire, sessionKey, suite, VeilRole.Responder);
        var odd = new byte[length];

        // An honest sender will not produce one.
        await Assert.ThrowsAsync<ArgumentException>(() => alice.SendMessageAsync(Epistle.Text("hi", Now) with { MessageId = odd }));
        await Assert.ThrowsAsync<ArgumentException>(() => alice.SendMessageAsync(Epistle.Text("hi", Now) with { InReplyTo = odd }));
        await Assert.ThrowsAsync<ArgumentException>(() => alice.SendAttestationAsync(odd));

        // And one built by hand is refused at decode, as a message id, a reply reference or an acknowledgement.
        var good = EpistleCodec.Encode(Epistle.Text("hi", Now) with { MessageId = new byte[16], InReplyTo = new byte[16] });
        Assert.Throws<CodexFormatException>(() => EpistleCodec.Decode(Resize(good, 0, length)));
        var replyAt = good.Length - 18; // … flag ‖ len ‖ reply(16) ‖ no-author flag
        Assert.Equal(1, good[replyAt - 1]);
        Assert.Throws<CodexFormatException>(() => EpistleCodec.Decode(Resize(good, replyAt, length)));

        var veil = new VeilCipher(sessionKey, suite, "epistle", VeilRole.Initiator);
        var ack = new CodexWriter();
        ack.WriteBytes(odd);
        ack.WriteByte(0); // no author
        var w = new CodexWriter();
        w.WriteByte(2);
        w.WriteBytes(veil.Seal(ack.ToArray(), [2]));
        bobWire.Incoming.Enqueue(w.ToArray());
        await Assert.ThrowsAsync<EpistleException>(() => bob.ReceiveAsync());
    }

    /// <summary>Replaces the 16-byte length-prefixed field at <paramref name="at"/> with one of <paramref name="length"/> zero bytes.</summary>
    private static byte[] Resize(byte[] encoded, int at, int length)
    {
        Assert.Equal(16, encoded[at]);
        var w = new CodexWriter();
        w.WriteBytes(new byte[length]);
        return [.. encoded[..at], .. w.ToArray(), .. encoded[(at + 17)..]];
    }

    private static RiteIdentity NewAuthor(ICryptoSuite suite)
    {
        var seal = suite.GenerateSeal();
        return new RiteIdentity(seal.PublicKey, seal.PrivateKey);
    }

    [Fact]
    public async Task AFrame_DeliveredTwice_IsRefusedTheSecondTime()
    {
        var suite = CryptoSuites.Secure();
        var sessionKey = RandomNumberGenerator.GetBytes(suite.Aead.KeySize);
        var aliceWire = new Wire();
        var bobWire = new Wire();
        var alice = new EpistleSession(aliceWire, sessionKey, suite, VeilRole.Initiator);
        var bob = new EpistleSession(bobWire, sessionKey, suite, VeilRole.Responder);

        await alice.SendMessageAsync(Epistle.Text("once", Now));
        var frame = aliceWire.Sent.Dequeue();

        bobWire.Incoming.Enqueue((byte[])frame.Clone());
        Assert.IsType<MessageReceived>(await bob.ReceiveAsync());

        // The very same bytes again: authentic, and refused — with nothing beneath the Veil doing the refusing.
        bobWire.Incoming.Enqueue(frame);
        await Assert.ThrowsAsync<EpistleException>(async () => await bob.ReceiveAsync());
    }

    [Fact]
    public async Task AnAcknowledgement_IsSignedByTheMemberWhoMakesIt_WhenTheyHaveAnIdentity()
    {
        var suite = CryptoSuites.Secure();
        var sessionKey = RandomNumberGenerator.GetBytes(suite.Aead.KeySize);
        var bobAuthor = NewAuthor(suite);
        var aliceWire = new Wire();
        var bobWire = new Wire();
        var alice = new EpistleSession(aliceWire, sessionKey, suite, VeilRole.Initiator, NewAuthor(suite), requireAuthor: true);
        var bob = new EpistleSession(bobWire, sessionKey, suite, VeilRole.Responder, bobAuthor, requireAuthor: true);
        var id = RandomNumberGenerator.GetBytes(16);

        await bob.SendAttestationAsync(id);
        aliceWire.Incoming.Enqueue(bobWire.Sent.Dequeue());
        var ack = Assert.IsType<AttestationReceived>(await alice.ReceiveAsync());
        Assert.Equal(id, ack.MessageId);
        Assert.Equal(bobAuthor.SealPublicKey, ack.AuthorSealPublicKey);

        // An application that relays hands it on as it came: it still names Bob, not whoever passed it along.
        await alice.SendAttestationAsync(ack);
        var carolWire = new Wire();
        var carol = new EpistleSession(carolWire, sessionKey, suite, VeilRole.Responder, requireAuthor: true);
        carolWire.Incoming.Enqueue(aliceWire.Sent.Dequeue());
        var relayed = Assert.IsType<AttestationReceived>(await carol.ReceiveAsync());
        Assert.Equal(bobAuthor.SealPublicKey, relayed.AuthorSealPublicKey);
    }

    [Fact]
    public async Task AnAcknowledgement_IsHeldToTheSameAuthorPolicyAsAMessage()
    {
        var suite = CryptoSuites.Secure();
        var sessionKey = RandomNumberGenerator.GetBytes(suite.Aead.KeySize);
        var author = NewAuthor(suite);
        var id = RandomNumberGenerator.GetBytes(16);

        async Task<byte[]> SentBy(EpistleSession session, Wire wire, Func<EpistleSession, Task> send)
        {
            await send(session);
            return wire.Sent.Dequeue();
        }

        async Task<EpistleEvent?> ReceivedBy(bool requireAuthor, byte[] frame)
        {
            var wire = new Wire();
            wire.Incoming.Enqueue(frame);
            return await new EpistleSession(wire, sessionKey, suite, VeilRole.Responder, requireAuthor: requireAuthor).ReceiveAsync();
        }

        // Unsigned: taken where signatures are optional, refused where they are required.
        var anonWire = new Wire();
        var anon = new EpistleSession(anonWire, sessionKey, suite, VeilRole.Initiator);
        var unsigned = await SentBy(anon, anonWire, s => s.SendAttestationAsync(id));
        Assert.Null(Assert.IsType<AttestationReceived>(await ReceivedBy(false, unsigned)).AuthorSealPublicKey);
        await Assert.ThrowsAsync<EpistleException>(() => ReceivedBy(true, unsigned));

        // A signature for one message id does not acknowledge another.
        var signedWire = new Wire();
        var signer = new EpistleSession(signedWire, sessionKey, suite, VeilRole.Initiator, author);
        await signer.SendAttestationAsync(id);
        var wire2 = new Wire();
        wire2.Incoming.Enqueue(signedWire.Sent.Dequeue());
        var genuine = Assert.IsType<AttestationReceived>(await new EpistleSession(wire2, sessionKey, suite, VeilRole.Responder).ReceiveAsync());
        var moved = genuine with { MessageId = RandomNumberGenerator.GetBytes(16) };
        await Assert.ThrowsAsync<EpistleException>(async () => await ReceivedBy(false, await SentBy(signer, signedWire, s => s.SendAttestationAsync(moved))));

        // Nor does a signature made over a message's bytes stand as an acknowledgement: the domains differ.
        var w = new CodexWriter();
        w.WriteBytes(id);
        w.WriteByte(0);
        var (key, sig) = RiteAuthor.Sign("epistle", w.ToArray(), author, suite);
        var crossed = new AttestationReceived(id, key, sig);
        await Assert.ThrowsAsync<EpistleException>(async () => await ReceivedBy(false, await SentBy(signer, signedWire, s => s.SendAttestationAsync(crossed))));
    }

    [Fact]
    public async Task TheFrameType_IsAuthenticated_SoAnAckCannotBeRelabelledAsAMessage()
    {
        var suite = CryptoSuites.Secure();
        var sessionKey = RandomNumberGenerator.GetBytes(suite.Aead.KeySize);
        var aliceWire = new Wire();
        var bobWire = new Wire();
        var alice = new EpistleSession(aliceWire, sessionKey, suite, VeilRole.Initiator);
        var bob = new EpistleSession(bobWire, sessionKey, suite, VeilRole.Responder);

        await alice.SendAttestationAsync(new byte[16]);
        var frame = aliceWire.Sent.Dequeue();

        // Untouched, it arrives as what it is.
        bobWire.Incoming.Enqueue((byte[])frame.Clone());
        Assert.IsType<AttestationReceived>(await bob.ReceiveAsync());

        // With the type byte — which sits outside the seal — changed from Attestation to Message, it is refused.
        frame[0] = 1;
        bobWire.Incoming.Enqueue(frame);
        await Assert.ThrowsAsync<EpistleException>(async () => await bob.ReceiveAsync());
    }

    [Fact]
    public async Task AFrame_ReflectedBackAtItsSender_IsRefused()
    {
        var suite = CryptoSuites.Secure();
        var sessionKey = RandomNumberGenerator.GetBytes(suite.Aead.KeySize);
        var wire = new Wire();
        var alice = new EpistleSession(wire, sessionKey, suite, VeilRole.Initiator);

        await alice.SendMessageAsync(Epistle.Text("mine", Now));
        wire.Incoming.Enqueue(wire.Sent.Dequeue());

        await Assert.ThrowsAsync<EpistleException>(async () => await alice.ReceiveAsync());
    }
}
