using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Arcanum;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Credential-enforced channel admission: knowing the Watchword derives the channel key, but an
/// owner-gated access mode (Sanction/Sealed) additionally requires proof of membership at Consecration —
/// so a leaked Watchword alone cannot confer membership.
/// </summary>
public class AdmissionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void Investiture_Codec_RoundTrips()
    {
        var suite = CryptoSuites.Secure();
        var owner = suite.GenerateSeal();
        var member = suite.GenerateSeal();
        var inv = Ownership.Invest(owner, [1, 2, 3], member.PublicKey, ArcanumRole.Member, Now.AddDays(-1), Now.AddDays(1), 42, suite);

        var decoded = Ownership.DecodeInvestiture(Ownership.EncodeInvestiture(inv));

        Assert.Equal(inv.MemberSigil, decoded.MemberSigil);
        Assert.Equal(inv.SerialNumber, decoded.SerialNumber);
        Assert.Equal(inv.Signature, decoded.Signature);
        Assert.True(Ownership.VerifyInvestiture(decoded, owner.PublicKey, [1, 2, 3], suite, Now));
    }

    private static async Task<(PairedPeer FromJoiner, PairedPeer FromHost)> PairAsync(CupriNode host, CupriNode joiner, CancellationToken ct)
    {
        var uri = host.IntoneUri(TimeSpan.FromHours(2), Now);
        Assert.True(IntonationUri.TryParse(uri, out var intonation, out _));
        var acceptTask = host.AcceptAsync(ct);
        var fromJoiner = await joiner.ConjoinAsync(intonation, Now, ct);
        var fromHost = await acceptTask;
        return (fromJoiner, fromHost);
    }

    [Fact]
    public async Task SealedChannel_ValidMembers_Consecrate_AndTalk()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var host = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);
        await using var joiner = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);
        var suite = host.Suite;

        var watchword = Watchword.Generate("GatedRoom");
        var channelId = Ownership.ChannelId(ArcanumKeys.Derive(watchword, suite));
        var owner = suite.GenerateSeal();
        var descriptor = Ownership.CreateDescriptor(owner, channelId, ArcanumEntry.Sealed, 1, suite, Now);
        var hostInv = Ownership.Invest(owner, channelId, host.Identity.Seal.PublicKey, ArcanumRole.Member, Now.AddDays(-1), Now.AddDays(1), 1, suite);
        var joinerInv = Ownership.Invest(owner, channelId, joiner.Identity.Seal.PublicKey, ArcanumRole.Member, Now.AddDays(-1), Now.AddDays(1), 2, suite);

        var (fromJoiner, fromHost) = await PairAsync(host, joiner, ct);
        await using var _1 = fromJoiner;
        await using var _2 = fromHost;

        var joinerConsecrate = joiner.ConsecrateAsync(fromJoiner, watchword, Now, new ArcanumAdmission { Descriptor = descriptor, MyInvestiture = joinerInv }, ct);
        var hostConsecrate = host.ConsecrateAsync(fromHost, watchword, Now, new ArcanumAdmission { Descriptor = descriptor, MyInvestiture = hostInv }, ct);
        await using var joinerChannel = await joinerConsecrate;
        await using var hostChannel = await hostConsecrate;

        await joinerChannel.SendTextAsync("we're both credentialed", Now, ct);
        var received = Assert.IsType<MessageReceived>(await hostChannel.Epistles.ReceiveAsync(ct));
        Assert.Equal("we're both credentialed", received.Epistle.AsText());
    }

    [Fact]
    public async Task SealedChannel_WrongMemberCredential_IsRejected()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var host = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);
        await using var joiner = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);
        var suite = host.Suite;

        var watchword = Watchword.Generate("GatedRoom");
        var channelId = Ownership.ChannelId(ArcanumKeys.Derive(watchword, suite));
        var owner = suite.GenerateSeal();
        var descriptor = Ownership.CreateDescriptor(owner, channelId, ArcanumEntry.Sealed, 1, suite, Now);
        var hostInv = Ownership.Invest(owner, channelId, host.Identity.Seal.PublicKey, ArcanumRole.Member, Now.AddDays(-1), Now.AddDays(1), 1, suite);

        var (fromJoiner, fromHost) = await PairAsync(host, joiner, ct);
        await using var _1 = fromJoiner;
        await using var _2 = fromHost;

        // The joiner presents a credential issued for a DIFFERENT member (the host), not itself.
        var joinerConsecrate = joiner.ConsecrateAsync(fromJoiner, watchword, Now, new ArcanumAdmission { Descriptor = descriptor, MyInvestiture = hostInv }, ct);
        var hostConsecrate = host.ConsecrateAsync(fromHost, watchword, Now, new ArcanumAdmission { Descriptor = descriptor, MyInvestiture = hostInv }, ct);

        // The host verifies the joiner's claim and rejects it (its Investiture names someone else).
        await Assert.ThrowsAsync<CupriNodeException>(async () => await hostConsecrate);
        try { await using var _ = await joinerConsecrate; } catch { /* joiner may also tear down */ }
    }

    [Fact]
    public async Task ApertureChannel_NeedsNoCredential_EvenWithDescriptor()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        await using var host = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);
        await using var joiner = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);
        var suite = host.Suite;

        var watchword = Watchword.Generate("OpenRoom");
        var channelId = Ownership.ChannelId(ArcanumKeys.Derive(watchword, suite));
        var owner = suite.GenerateSeal();
        var descriptor = Ownership.CreateDescriptor(owner, channelId, ArcanumEntry.Aperture, 1, suite, Now);

        var (fromJoiner, fromHost) = await PairAsync(host, joiner, ct);
        await using var _1 = fromJoiner;
        await using var _2 = fromHost;

        // Open access: neither side holds an Investiture, but the Watchword suffices.
        var joinerConsecrate = joiner.ConsecrateAsync(fromJoiner, watchword, Now, new ArcanumAdmission { Descriptor = descriptor }, ct);
        var hostConsecrate = host.ConsecrateAsync(fromHost, watchword, Now, new ArcanumAdmission { Descriptor = descriptor }, ct);
        await using var joinerChannel = await joinerConsecrate;
        await using var hostChannel = await hostConsecrate;

        await joinerChannel.SendTextAsync("open channel", Now, ct);
        var received = Assert.IsType<MessageReceived>(await hostChannel.Epistles.ReceiveAsync(ct));
        Assert.Equal("open channel", received.Epistle.AsText());
    }
}
