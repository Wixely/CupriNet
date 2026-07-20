using System.Net;
using CupriNet.Abstractions;
using CupriNet.Arcanum;
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

        // Both sides exchange all messages, then each rejects the other's confirmation.
        await Assert.ThrowsAsync<ConsecrationException>(async () => await initiate);
        await Assert.ThrowsAsync<ConsecrationException>(async () => await accept);
    }
}
