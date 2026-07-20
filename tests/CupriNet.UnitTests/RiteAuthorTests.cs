using System.Net;
using System.Security.Cryptography;
using System.Text;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

/// <summary>
/// The authenticated-authorship envelope: a channel session signs each rite with its Seal so that a
/// relaying member (or the transport) cannot forge or rewrite the author. Covers all three rites.
/// </summary>
public class RiteAuthorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static RiteIdentity NewAuthor(ICryptoSuite suite)
    {
        var seal = suite.GenerateSeal();
        return new RiteIdentity(seal.PublicKey, seal.PrivateKey);
    }

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
    public void RiteAuthor_Sign_Verifies_AndDetectsTamperAndCrossDomain()
    {
        var suite = CryptoSuites.Secure();
        var author = NewAuthor(suite);
        var content = Encoding.UTF8.GetBytes("the exact bytes that were signed");

        var (key, sig) = RiteAuthor.Sign("epistle", content, author, suite);

        Assert.True(RiteAuthor.Verify("epistle", content, key, sig, suite, requireAuthor: true));
        // Tampered content fails.
        Assert.False(RiteAuthor.Verify("epistle", Encoding.UTF8.GetBytes("different bytes"), key, sig, suite, requireAuthor: true));
        // A signature minted for one rite cannot be replayed as another (domain separation).
        Assert.False(RiteAuthor.Verify("conduit", content, key, sig, suite, requireAuthor: true));
        // The author Sigil is the Sigil of the Seal key.
        Assert.Equal(Sigil.FromSealPublicKey(author.SealPublicKey), RiteAuthor.AuthorSigil(key));
    }

    [Fact]
    public void RiteAuthor_AbsentEnvelope_AcceptedOnlyWhenNotRequired()
    {
        var suite = CryptoSuites.Secure();
        var content = Encoding.UTF8.GetBytes("unsigned");
        Assert.True(RiteAuthor.Verify("epistle", content, null, null, suite, requireAuthor: false));
        Assert.False(RiteAuthor.Verify("epistle", content, null, null, suite, requireAuthor: true));
    }

    [Fact]
    public void Codecs_RoundTrip_AuthorEnvelope()
    {
        var suite = CryptoSuites.Secure();
        var author = NewAuthor(suite);

        var epistle = Epistle.Text("hi", Now) with { AuthorSealPublicKey = author.SealPublicKey, AuthorSignature = [1, 2, 3] };
        var e2 = EpistleCodec.Decode(EpistleCodec.Encode(epistle));
        Assert.Equal(author.SealPublicKey, e2.AuthorSealPublicKey);
        Assert.Equal(new byte[] { 1, 2, 3 }, e2.AuthorSignature);

        var frame = new ConduitFrame { ProtocolId = 1, SchemaVersion = 1, Flags = 0, Payload = [4, 5], AuthorSealPublicKey = author.SealPublicKey, AuthorSignature = [6, 7] };
        var f2 = ConduitCodec.Decode(ConduitCodec.Encode(frame));
        Assert.Equal(author.SealPublicKey, f2.AuthorSealPublicKey);
        Assert.Equal(new byte[] { 6, 7 }, f2.AuthorSignature);
    }

    [Fact]
    public async Task EpistleSession_Signs_AndReceiverVerifies_ExposingAuthorSigil()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();
        var sessionKey = RandomNumberGenerator.GetBytes(suite.Aead.KeySize);
        var alice = NewAuthor(suite);

        var (va, vb, listener) = await ConnectedPairAsync(ct);
        await using var _a = va;
        await using var _b = vb;
        await using var _l = listener;

        var sender = new EpistleSession(va, sessionKey, suite, alice, requireAuthor: true);
        var receiver = new EpistleSession(vb, sessionKey, suite, author: null, requireAuthor: true);

        await sender.SendMessageAsync(Epistle.Text("signed hello", Now), ct);
        var message = Assert.IsType<MessageReceived>(await receiver.ReceiveAsync(ct));

        Assert.NotNull(message.Epistle.AuthorSealPublicKey);
        Assert.Equal(Sigil.FromSealPublicKey(alice.SealPublicKey), RiteAuthor.AuthorSigil(message.Epistle.AuthorSealPublicKey!));
        Assert.Equal("signed hello", message.Epistle.AsText());
    }

    [Fact]
    public async Task EpistleSession_RequireAuthor_RejectsUnsigned()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();
        var sessionKey = RandomNumberGenerator.GetBytes(suite.Aead.KeySize);

        var (va, vb, listener) = await ConnectedPairAsync(ct);
        await using var _a = va;
        await using var _b = vb;
        await using var _l = listener;

        var sender = new EpistleSession(va, sessionKey, suite, author: null, requireAuthor: false); // sends unsigned
        var receiver = new EpistleSession(vb, sessionKey, suite, author: null, requireAuthor: true);

        await sender.SendMessageAsync(Epistle.Text("no signature", Now), ct);
        await Assert.ThrowsAsync<EpistleException>(async () => await receiver.ReceiveAsync(ct));
    }

    [Fact]
    public async Task EpistleSession_Relay_PreservesOriginalAuthor_NotTheRelayer()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();
        var sessionKey = RandomNumberGenerator.GetBytes(suite.Aead.KeySize);
        var alice = NewAuthor(suite);
        var relay = NewAuthor(suite);

        // Two hops: Alice -> Relay, Relay -> Carol.
        var (va, vRelayIn, l1) = await ConnectedPairAsync(ct);
        var (vRelayOut, vCarol, l2) = await ConnectedPairAsync(ct);
        await using var _1 = va; await using var _2 = vRelayIn; await using var _3 = l1;
        await using var _4 = vRelayOut; await using var _5 = vCarol; await using var _6 = l2;

        var aliceSession = new EpistleSession(va, sessionKey, suite, alice, requireAuthor: true);
        var relayIn = new EpistleSession(vRelayIn, sessionKey, suite, relay, requireAuthor: true);
        var relayOut = new EpistleSession(vRelayOut, sessionKey, suite, relay, requireAuthor: true);
        var carol = new EpistleSession(vCarol, sessionKey, suite, author: null, requireAuthor: true);

        await aliceSession.SendMessageAsync(Epistle.Text("relay me", Now), ct);
        var atRelay = Assert.IsType<MessageReceived>(await relayIn.ReceiveAsync(ct));

        // Relay forwards the received Epistle verbatim — it must NOT be re-signed as the relayer.
        await relayOut.SendMessageAsync(atRelay.Epistle, ct);
        var atCarol = Assert.IsType<MessageReceived>(await carol.ReceiveAsync(ct));

        Assert.Equal(Sigil.FromSealPublicKey(alice.SealPublicKey), RiteAuthor.AuthorSigil(atCarol.Epistle.AuthorSealPublicKey!));
        Assert.NotEqual(Sigil.FromSealPublicKey(relay.SealPublicKey), RiteAuthor.AuthorSigil(atCarol.Epistle.AuthorSealPublicKey!));
        Assert.Equal("relay me", atCarol.Epistle.AsText());
    }

    [Fact]
    public void Reliquary_SignedManifest_VerifiesAndDetectsTamper()
    {
        var suite = CryptoSuites.Secure();
        var author = NewAuthor(suite);
        var manifest = ReliquaryBuilder.Build([("note.txt", Encoding.UTF8.GetBytes("hello world"))], 4, suite);

        var signed = ReliquaryCodec.Sign(manifest, author, suite);
        var roundTripped = ReliquaryCodec.Decode(ReliquaryCodec.Encode(signed));

        Assert.True(ReliquaryCodec.VerifyAuthor(roundTripped, suite, requireAuthor: true));
        // An unsigned manifest is rejected when authorship is required.
        Assert.False(ReliquaryCodec.VerifyAuthor(manifest, suite, requireAuthor: true));
        // Tampering with the transfer id (keeping the old signature) fails verification.
        var tampered = signed with { TransferId = RandomNumberGenerator.GetBytes(16) };
        Assert.False(ReliquaryCodec.VerifyAuthor(tampered, suite, requireAuthor: true));
    }
}
