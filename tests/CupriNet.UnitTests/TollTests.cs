using System.Net;
using CupriNet.Codex;
using CupriNet.Conjunction;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

/// <summary>The pre-handshake Toll: a stateless cookie the responder issues and verifies before any Noise state.</summary>
public class TollTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static async Task<(VesselSession Initiator, VesselSession Responder, VesselListener Listener)> PairAsync(CancellationToken ct)
    {
        var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        var initiator = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        var responder = await acceptTask;
        return (initiator, responder, listener);
    }

    [Fact]
    public async Task Toll_HappyPath_InitiatorSolves_ResponderAccepts()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var (initiator, responder, listener) = await PairAsync(ct);
        await using var _i = initiator;
        await using var _r = responder;
        await using var _l = listener;

        var secret = Toll.NewSecret();
        var responderTask = Toll.IssueAndVerifyAsync(responder, secret, responder.RemoteEndPoint, Now, ct);
        var initiatorTask = Toll.SolveAsync(initiator, ct);

        await Task.WhenAll(responderTask, initiatorTask); // completes without throwing
    }

    [Fact]
    public async Task Toll_ForgedCookie_IsRejected()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var (initiator, responder, listener) = await PairAsync(ct);
        await using var _i = initiator;
        await using var _r = responder;
        await using var _l = listener;

        var secret = Toll.NewSecret();
        var responderTask = Toll.IssueAndVerifyAsync(responder, secret, responder.RemoteEndPoint, Now, ct);

        // Initiator reads the challenge (min,max version range) but echoes a valid version with a tampered cookie.
        var challenge = await initiator.ReceiveAsync(ct);
        var r = new CodexReader(challenge!.Value.Payload);
        var min = r.ReadVarUInt();
        _ = r.ReadVarUInt(); // max
        var issuedAt = r.ReadUInt64();
        var cookie = r.ReadBytes().ToArray();
        cookie[0] ^= 0xFF; // corrupt it

        var w = new CodexWriter();
        w.WriteVarUInt(min);       // echo an accepted version, so rejection is due to the cookie, not the version
        w.WriteUInt64(issuedAt);
        w.WriteBytes(cookie);
        await initiator.SendAsync(0, w.ToArray(), ct);

        await Assert.ThrowsAsync<NoiseConjunctionException>(async () => await responderTask);
    }

    [Fact]
    public async Task Toll_EchoedUnsupportedVersion_IsRejected()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var (initiator, responder, listener) = await PairAsync(ct);
        await using var _i = initiator;
        await using var _r = responder;
        await using var _l = listener;

        var secret = Toll.NewSecret();
        var responderTask = Toll.IssueAndVerifyAsync(responder, secret, responder.RemoteEndPoint, Now, ct);

        // Echo an ordinal the responder's CupriMark catalogue does not speak — rejected before the cookie is even checked.
        var challenge = await initiator.ReceiveAsync(ct);
        var r = new CodexReader(challenge!.Value.Payload);
        _ = r.ReadVarUInt(); // min
        _ = r.ReadVarUInt(); // max
        var issuedAt = r.ReadUInt64();
        var cookie = r.ReadBytes().ToArray();

        var w = new CodexWriter();
        w.WriteVarUInt(9999); // unsupported version
        w.WriteUInt64(issuedAt);
        w.WriteBytes(cookie);
        await initiator.SendAsync(0, w.ToArray(), ct);

        await Assert.ThrowsAsync<NoiseConjunctionException>(async () => await responderTask);
    }

    [Fact]
    public async Task Toll_Solve_RejectsUnsupportedAdvertisedRange()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var (initiator, responder, listener) = await PairAsync(ct);
        await using var _i = initiator;
        await using var _r = responder;
        await using var _l = listener;

        // Craft a challenge that advertises a version range this build can't negotiate — the initiator refuses.
        var w = new CodexWriter();
        w.WriteVarUInt(9998);
        w.WriteVarUInt(9999);
        w.WriteUInt64((ulong)Now.ToUnixTimeSeconds());
        w.WriteBytes(new byte[Toll.CookieSize]);
        await responder.SendAsync(0, w.ToArray(), ct);

        await Assert.ThrowsAsync<NoiseConjunctionException>(async () => await Toll.SolveAsync(initiator, ct));
    }
}
