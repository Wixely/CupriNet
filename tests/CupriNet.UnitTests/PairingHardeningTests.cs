using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Arcanum;
using CupriNet.Concordance;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// What a peer that speaks the protocol by hand — rather than through a well-behaved node — can and cannot do
/// to the node on the other end: reflect an admission claim, hand over somebody else's overlay record, or ask a
/// Divination for one key and be answered for another.
/// </summary>
public class PairingHardeningTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static Task<CupriNode> NewNodeAsync(CancellationToken ct)
        => CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "hardening.test", EnableReflexiveDiscovery = false }, ct);

    /// <summary>Dials a node and completes the Toll, the Conjunction and the session-kind marker by hand.</summary>
    private static async Task<NoiseVessel> DialRawAsync(CupriNode node, NodeIdentity identity, byte kind, CancellationToken ct)
    {
        var raw = await TcpVessel.ConnectAsync("127.0.0.1", node.LocalEndPoint.Port, cancellationToken: ct);
        await Toll.SolveAsync(raw, ct);
        var conjunction = await NoiseConjunction.InitiateAsync(
            raw, identity, node.Network, node.Suite, expectedPeer: node.Identity.Sigil, cancellationToken: ct);
        await conjunction.Vessel.SendAsync(OverlayControl.Stream, new[] { kind }, ct);
        return conjunction.Vessel;
    }

    private static PeerRecord RecordFor(NodeIdentity identity, string host, ICryptoSuite suite)
        => PeerRecordSigner.Create(identity, [new Beacon(EndpointKind.Host, host, 4000)], 1, PeerCapabilities.ChannelProvider, suite, Now);

    [Fact]
    public async Task Bootstrap_AdmitsThePeersOwnRecord_ButNotAThirdPartys()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var node = await NewNodeAsync(ct);
        var suite = node.Suite;
        var thirdParty = NodeIdentity.Generate(suite);

        // A peer that answers "here is my record" with a validly signed record for someone else.
        var liar = NodeIdentity.Generate(suite);
        await using (var vessel = await DialRawAsync(node, liar, OverlayControl.KindChannel, ct))
        {
            await vessel.SendAsync(PeerExchange.DefaultStream, PeerRecordCodec.Encode(RecordFor(thirdParty, "10.1.1.1", suite)), ct);
            Assert.NotNull(await vessel.ReceiveAsync(ct)); // the node's own record: it has finished with ours
        }
        Assert.Null(node.Constellation.Get(thirdParty.Sigil));

        // An honest peer's own record is still admitted.
        var honest = NodeIdentity.Generate(suite);
        await using (var vessel = await DialRawAsync(node, honest, OverlayControl.KindChannel, ct))
        {
            await vessel.SendAsync(PeerExchange.DefaultStream, PeerRecordCodec.Encode(RecordFor(honest, "10.1.2.1", suite)), ct);
            Assert.NotNull(await vessel.ReceiveAsync(ct));
        }
        Assert.NotNull(node.Constellation.Get(honest.Sigil));
    }

    [Fact]
    public async Task Divine_IsAnsweredForTheRequestedTarget()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var node = await NewNodeAsync(ct);
        var suite = node.Suite;

        for (var i = 0; i < 12; i++)
            Assert.True(node.AdmitPeer(RecordFor(NodeIdentity.Generate(suite), $"10.2.{i}.1", suite), Now));

        var target = RoutingKey.FromToken("a divination target"u8);
        var expected = node.Constellation.ClosestTo(target, OverlayControl.MaxAugury).Select(r => r.Sigil).ToList();

        await using var vessel = await DialRawAsync(node, NodeIdentity.Generate(suite), OverlayControl.KindControl, ct);
        await vessel.SendAsync(OverlayControl.Stream, OverlayControl.DivineRequest(target), ct);
        var response = await vessel.ReceiveAsync(ct);

        var answered = OverlayControl.ParsePeerRecords(response!.Value.Payload, suite).Select(r => r.Sigil).ToList();
        Assert.Equal(12, answered.Count);
        Assert.Equal(expected, answered); // closest-first to the key that was asked for
    }

    [Fact]
    public async Task SealedChannel_AReflectedAdmissionClaim_IsRejected()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var intruder = await NewNodeAsync(ct); // knows the Watchword; holds no Investiture
        await using var member = await NewNodeAsync(ct);
        var suite = member.Suite;

        var watchword = Watchword.Generate("GatedRoom");
        var keys = ArcanumKeys.Derive(watchword, suite);
        var channelId = Ownership.ChannelId(keys);
        var owner = suite.GenerateSeal();
        var descriptor = Ownership.CreateDescriptor(owner, channelId, ArcanumEntry.Sealed, 1, suite, Now);
        var memberSeal = suite.GenerateSeal();
        var memberPersona = new RiteIdentity(memberSeal.PublicKey, memberSeal.PrivateKey);
        var memberInv = Ownership.Invest(owner, channelId, memberPersona.SealPublicKey, ArcanumRole.Member, Now.AddDays(-1), Now.AddDays(1), 1, suite);

        // The member dials the intruder, so the member is the initiator and sends its claim first.
        var uri = intruder.IntoneUri(TimeSpan.FromHours(2), Now);
        Assert.True(IntonationUri.TryParse(uri, out var intonation, out _));
        var acceptTask = intruder.AcceptAsync(ct);
        await using var fromMember = await member.ConjoinAsync(intonation, Now, ct);
        await using var fromIntruder = await acceptTask;

        var memberConsecrate = member.ConsecrateAsync(fromMember, watchword, Now,
            new ConsecrateOptions { ChannelIdentity = memberPersona, Admission = new ArcanumAdmission { Descriptor = descriptor, MyInvestiture = memberInv } }, ct);

        // The intruder completes Consecration honestly (it has the Watchword), then echoes the member's own claim.
        await ConsecrationHandshake.AcceptAsync(fromIntruder.Vessel, keys, intruder.Identity.Sigil, fromIntruder.PeerSigil, Now, suite, cancellationToken: ct);
        var claim = await fromIntruder.Vessel.ReceiveAsync(ct);
        Assert.Equal(ConsecrationHandshake.ChannelStream, claim!.Value.StreamId);
        await fromIntruder.Vessel.SendAsync(ConsecrationHandshake.ChannelStream, claim.Value.Payload, ct);

        await Assert.ThrowsAsync<CupriNodeException>(async () => await memberConsecrate);
    }
}
