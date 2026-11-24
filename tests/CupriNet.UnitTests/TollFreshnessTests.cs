using System.Net;
using CupriNet.Codex;
using CupriNet.Conjunction;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

/// <summary>
/// The Toll's freshness window is measured to the moment the echo arrives: a cookie held past
/// <see cref="Toll.MaxAge"/> is refused, and so is one stamped later than the present.
/// </summary>
public class TollFreshnessTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<(VesselSession Initiator, VesselSession Responder, VesselListener Listener)> PairAsync(CancellationToken ct)
    {
        var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        var initiator = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        var responder = await acceptTask;
        return (initiator, responder, listener);
    }

    /// <summary>Reads a challenge and returns the echo an honest initiator would send for it.</summary>
    private static async Task<byte[]> ReadChallengeAsEchoAsync(VesselSession initiator, CancellationToken ct)
    {
        var challenge = await initiator.ReceiveAsync(ct);
        var r = new CodexReader(challenge!.Value.Payload);
        var min = r.ReadVarUInt();
        _ = r.ReadVarUInt(); // max
        var issuedAt = r.ReadUInt64();
        var cookie = r.ReadBytes().ToArray();

        var w = new CodexWriter();
        w.WriteVarUInt(min);
        w.WriteUInt64(issuedAt);
        w.WriteBytes(cookie);
        return w.ToArray();
    }

    [Fact]
    public async Task Echo_HeldPastMaxAge_IsRejected()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var (initiator, responder, listener) = await PairAsync(ct);
        await using var _i = initiator;
        await using var _r = responder;
        await using var _l = listener;

        var clock = new ManualClock(Start);
        var responderTask = Toll.IssueAndVerifyAsync(responder, Toll.NewSecret(), responder.RemoteEndPoint, clock, ct);

        var echo = await ReadChallengeAsEchoAsync(initiator, ct);
        clock.Now = Start + Toll.MaxAge + TimeSpan.FromSeconds(1); // the initiator sat on the cookie
        await initiator.SendAsync(0, echo, ct);

        await Assert.ThrowsAsync<NoiseConjunctionException>(async () => await responderTask);
    }

    [Fact]
    public async Task Echo_WithinMaxAge_IsAccepted()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var (initiator, responder, listener) = await PairAsync(ct);
        await using var _i = initiator;
        await using var _r = responder;
        await using var _l = listener;

        var clock = new ManualClock(Start);
        var responderTask = Toll.IssueAndVerifyAsync(responder, Toll.NewSecret(), responder.RemoteEndPoint, clock, ct);

        var echo = await ReadChallengeAsEchoAsync(initiator, ct);
        clock.Now = Start + Toll.MaxAge; // the boundary is still acceptable
        await initiator.SendAsync(0, echo, ct);

        await responderTask;
    }

    [Fact]
    public async Task Cookie_StampedLaterThanThePresent_IsRejected()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var secret = Toll.NewSecret();

        // A genuine cookie for this address, minted while the responder's clock read 20 s ahead.
        byte[] laterEcho;
        {
            var (initiator, responder, listener) = await PairAsync(ct);
            await using var _i = initiator;
            await using var _r = responder;
            await using var _l = listener;
            var ahead = new ManualClock(Start.AddSeconds(20));
            var minting = Toll.IssueAndVerifyAsync(responder, secret, responder.RemoteEndPoint, ahead, ct);
            laterEcho = await ReadChallengeAsEchoAsync(initiator, ct);
            await initiator.SendAsync(0, laterEcho, ct);
            await minting;
        }

        // Presented on a connection whose clock reads `Start`, the cookie is from the future.
        {
            var (initiator, responder, listener) = await PairAsync(ct);
            await using var _i = initiator;
            await using var _r = responder;
            await using var _l = listener;
            var responderTask = Toll.IssueAndVerifyAsync(responder, secret, responder.RemoteEndPoint, new ManualClock(Start), ct);
            _ = await initiator.ReceiveAsync(ct); // discard this connection's own challenge
            await initiator.SendAsync(0, laterEcho, ct);

            await Assert.ThrowsAsync<NoiseConjunctionException>(async () => await responderTask);
        }
    }
}
