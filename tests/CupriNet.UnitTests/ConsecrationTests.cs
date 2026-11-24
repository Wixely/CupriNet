using System.Net;
using CupriNet.Abstractions;
using CupriNet.Arcanum;
using CupriNet.Codex;
using CupriNet.Core;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

public class ConsecrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static Watchword Fixed(string name, string salt = "AAAAAAAAAAAAAAAAAAAAAA")
    {
        Assert.True(Watchword.TryParse($"{name}#{salt}", out var w));
        return w;
    }

    private static async Task<(VesselSession Initiator, VesselSession Responder, VesselListener Listener)> ConnectedPairAsync(CancellationToken ct)
    {
        var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        var initiator = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        var responder = await acceptTask;
        return (initiator, responder, listener);
    }

    [Fact]
    public async Task SameWatchword_DerivesIdenticalSessionKey()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();
        var keys = ArcanumKeys.Derive(Fixed("Dungeons&Dragons"), suite);

        var memberA = NodeIdentity.Generate(suite).Sigil;
        var memberB = NodeIdentity.Generate(suite).Sigil;

        var (i, r, listener) = await ConnectedPairAsync(ct);
        await using var _i = i;
        await using var _r = r;
        await using var _l = listener;

        var initiate = ConsecrationHandshake.InitiateAsync(i, keys, memberA, memberB, Now, suite, cancellationToken: ct);
        var accept = ConsecrationHandshake.AcceptAsync(r, keys, memberB, memberA, Now, suite, cancellationToken: ct);

        var a = await initiate;
        var b = await accept;

        Assert.Equal(a.Epoch, b.Epoch);
        Assert.Equal(a.SessionKey, b.SessionKey); // shared Veil session key
        Assert.Equal(32, a.SessionKey.Length);
        // Both members range-negotiated the channel-handshake version (v2) via CupriMark. That they derived
        // the SAME session key also proves both folded an identical negotiation digest into the transcript.
        Assert.Equal(2, (int)a.ConsecrationVersion);
        Assert.Equal(2, (int)b.ConsecrationVersion);
    }

    [Fact]
    public async Task OneEpochOfSkew_StillConsecrates_AdoptingInitiatorEpoch()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Simulacrum(); // KDF/Hash are real even here, so confirmation is meaningful
        var keys = ArcanumKeys.Derive(Fixed("Gaming"), suite);
        var a = NodeIdentity.Generate(suite).Sigil;
        var b = NodeIdentity.Generate(suite).Sigil;

        var (i, r, listener) = await ConnectedPairAsync(ct);
        await using var _i = i;
        await using var _r = r;
        await using var _l = listener;

        var initiatorNow = Now;
        var responderNow = Now.AddSeconds(Glyph.DefaultTurningSeconds); // one epoch ahead

        var initiate = ConsecrationHandshake.InitiateAsync(i, keys, a, b, initiatorNow, suite, cancellationToken: ct);
        var accept = ConsecrationHandshake.AcceptAsync(r, keys, b, a, responderNow, suite, cancellationToken: ct);

        var ra = await initiate;
        var rb = await accept;

        Assert.Equal(ra.Epoch, rb.Epoch);
        Assert.Equal(Glyph.Epoch(initiatorNow), ra.Epoch); // initiator's epoch wins
        Assert.Equal(ra.SessionKey, rb.SessionKey);
    }

    [Fact]
    public async Task DifferentWatchword_FailsKeyConfirmation()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();
        var mine = ArcanumKeys.Derive(Fixed("Gaming"), suite);
        var theirs = ArcanumKeys.Derive(Fixed("Politics"), suite);
        var a = NodeIdentity.Generate(suite).Sigil;
        var b = NodeIdentity.Generate(suite).Sigil;

        var (i, r, listener) = await ConnectedPairAsync(ct);
        await using var _i = i;
        await using var _r = r;
        await using var _l = listener;

        var initiate = ConsecrationHandshake.InitiateAsync(i, mine, a, b, Now, suite, cancellationToken: ct);
        var accept = ConsecrationHandshake.AcceptAsync(r, theirs, b, a, Now, suite, cancellationToken: ct);

        // The responder rejects the initiator's confirmation and says so; the initiator fails at once on that.
        await Assert.ThrowsAsync<ConsecrationException>(async () => await initiate);
        await Assert.ThrowsAsync<ConsecrationException>(async () => await accept);
    }

    private static byte[] Hello(long epoch, byte[] nonce)
    {
        var w = new CodexWriter();
        w.WriteByte(1);       // Hello
        w.WriteByte(1);       // envelope version
        w.WriteVarUInt(2);    // advertised Consecration range [2, 2]
        w.WriteVarUInt(2);
        w.WriteUInt64((ulong)epoch);
        w.WriteBytes(nonce);
        return w.ToArray();
    }

    private static byte[] Confirm(byte[] tag)
    {
        var w = new CodexWriter();
        w.WriteByte(2);       // Confirm
        w.WriteBytes(tag);
        return w.ToArray();
    }

    [Fact]
    public async Task ACaller_WhoCannotConfirm_GetsARefusal_AndNothingDerivedFromTheWatchword()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();
        var keys = ArcanumKeys.Derive(Fixed("Gaming"), suite);
        var a = NodeIdentity.Generate(suite).Sigil;
        var b = NodeIdentity.Generate(suite).Sigil;

        var (i, r, listener) = await ConnectedPairAsync(ct);
        await using var _i = i;
        await using var _r = r;
        await using var _l = listener;

        var accept = ConsecrationHandshake.AcceptAsync(r, keys, b, a, Now, suite, cancellationToken: ct);

        // Someone who does not know the Watchword plays the initiator by hand: a proper Hello, then a guess.
        await i.SendAsync(ConsecrationHandshake.ChannelStream, Hello(Glyph.Epoch(Now), new byte[16]), ct);
        var hello = await i.ReceiveAsync(ct);
        Assert.Equal(1, hello!.Value.Payload[0]);
        await i.SendAsync(ConsecrationHandshake.ChannelStream, Confirm(new byte[32]), ct);

        await Assert.ThrowsAsync<ConsecrationException>(async () => await accept);

        // All that comes back is the one-byte refusal — not the responder's own confirmation tag, which would
        // have been something to test Watchword guesses against.
        var answer = await i.ReceiveAsync(ct);
        Assert.Equal(new byte[] { 3 }, answer!.Value.Payload);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(0)]
    public async Task AHello_WithANonceOfTheWrongLength_IsRefused(int nonceLength)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();
        var keys = ArcanumKeys.Derive(Fixed("Gaming"), suite);

        var (i, r, listener) = await ConnectedPairAsync(ct);
        await using var _i = i;
        await using var _r = r;
        await using var _l = listener;

        var accept = ConsecrationHandshake.AcceptAsync(r, keys, NodeIdentity.Generate(suite).Sigil, NodeIdentity.Generate(suite).Sigil, Now, suite, cancellationToken: ct);
        await i.SendAsync(ConsecrationHandshake.ChannelStream, Hello(Glyph.Epoch(Now), new byte[nonceLength]), ct);

        await Assert.ThrowsAsync<ConsecrationException>(async () => await accept);
    }

    [Fact]
    public async Task AResponder_WhoseClockIsFarOff_IsRefusedByTheInitiator()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();
        var keys = ArcanumKeys.Derive(Fixed("Gaming"), suite);

        var (i, r, listener) = await ConnectedPairAsync(ct);
        await using var _i = i;
        await using var _r = r;
        await using var _l = listener;

        var initiate = ConsecrationHandshake.InitiateAsync(i, keys, NodeIdentity.Generate(suite).Sigil, NodeIdentity.Generate(suite).Sigil, Now, suite, cancellationToken: ct);

        // A hand-driven responder that answers with an epoch well outside the window.
        var hello = await r.ReceiveAsync(ct);
        Assert.Equal(1, hello!.Value.Payload[0]);
        await r.SendAsync(ConsecrationHandshake.ChannelStream, Hello(Glyph.Epoch(Now) + 5, new byte[16]), ct);

        await Assert.ThrowsAsync<ConsecrationException>(async () => await initiate);
    }
}
