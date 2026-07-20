using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Arcanum;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Credential-enforced admission AND layer separation: a node cultivates the overlay under its Sigil but
/// speaks and holds channel membership under a distinct persona, so what it says on the network is
/// unlinkable to its overlay identity. Owner-gated modes (Sanction/Sealed) require the persona to prove
/// membership; the Watchword alone never confers it.
/// </summary>
public class AdmissionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static RiteIdentity NewPersona(ICryptoSuite suite)
    {
        var seal = suite.GenerateSeal();
        return new RiteIdentity(seal.PublicKey, seal.PrivateKey);
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

    [Fact]
    public async Task SealedChannel_PersonaMembers_Consecrate_Talk_AndStayUnlinkableToOverlay()
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

        // Each node speaks under a persona distinct from its overlay identity; the owner credentials the personas.
        var hostPersona = NewPersona(suite);
        var joinerPersona = NewPersona(suite);
        var hostInv = Ownership.Invest(owner, channelId, hostPersona.SealPublicKey, ArcanumRole.Member, Now.AddDays(-1), Now.AddDays(1), 1, suite);
        var joinerInv = Ownership.Invest(owner, channelId, joinerPersona.SealPublicKey, ArcanumRole.Member, Now.AddDays(-1), Now.AddDays(1), 2, suite);

        var (fromJoiner, fromHost) = await PairAsync(host, joiner, ct);
        await using var _1 = fromJoiner;
        await using var _2 = fromHost;

        var joinerConsecrate = joiner.ConsecrateAsync(fromJoiner, watchword, Now,
            new ConsecrateOptions { ChannelIdentity = joinerPersona, Admission = new ArcanumAdmission { Descriptor = descriptor, MyInvestiture = joinerInv } }, ct);
        var hostConsecrate = host.ConsecrateAsync(fromHost, watchword, Now,
            new ConsecrateOptions { ChannelIdentity = hostPersona, Admission = new ArcanumAdmission { Descriptor = descriptor, MyInvestiture = hostInv } }, ct);
        await using var joinerChannel = await joinerConsecrate;
        await using var hostChannel = await hostConsecrate;

        await joinerChannel.SendTextAsync("credentialed and pseudonymous", Now, ct);
        var received = Assert.IsType<MessageReceived>(await hostChannel.Epistles.ReceiveAsync(ct));
        Assert.Equal("credentialed and pseudonymous", received.Epistle.AsText());

        // Separation: the author is the joiner's PERSONA, not its overlay Sigil.
        var authorSigil = RiteAuthor.AuthorSigil(received.Epistle.AuthorSealPublicKey!);
        Assert.Equal(Sigil.FromSealPublicKey(joinerPersona.SealPublicKey), authorSigil);
        Assert.NotEqual(joiner.Identity.Sigil, authorSigil);
    }

    [Fact]
    public async Task SealedChannel_CredentialForADifferentPersona_IsRejected()
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

        var hostPersona = NewPersona(suite);
        var joinerPersona = NewPersona(suite);
        var hostInv = Ownership.Invest(owner, channelId, hostPersona.SealPublicKey, ArcanumRole.Member, Now.AddDays(-1), Now.AddDays(1), 1, suite);

        var (fromJoiner, fromHost) = await PairAsync(host, joiner, ct);
        await using var _1 = fromJoiner;
        await using var _2 = fromHost;

        // The joiner speaks under joinerPersona but presents a credential issued for hostPersona.
        var joinerConsecrate = joiner.ConsecrateAsync(fromJoiner, watchword, Now,
            new ConsecrateOptions { ChannelIdentity = joinerPersona, Admission = new ArcanumAdmission { Descriptor = descriptor, MyInvestiture = hostInv } }, ct);
        var hostConsecrate = host.ConsecrateAsync(fromHost, watchword, Now,
            new ConsecrateOptions { ChannelIdentity = hostPersona, Admission = new ArcanumAdmission { Descriptor = descriptor, MyInvestiture = hostInv } }, ct);

        await Assert.ThrowsAsync<CupriNodeException>(async () => await hostConsecrate);
        try { await using var _ = await joinerConsecrate; } catch { /* joiner may also tear down */ }
    }

    [Fact]
    public async Task ApertureChannel_NeedsNoCredential_ButStillSpeaksUnderPersona()
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
        var joinerPersona = NewPersona(suite);
        var hostPersona = NewPersona(suite);

        var (fromJoiner, fromHost) = await PairAsync(host, joiner, ct);
        await using var _1 = fromJoiner;
        await using var _2 = fromHost;

        var joinerConsecrate = joiner.ConsecrateAsync(fromJoiner, watchword, Now,
            new ConsecrateOptions { ChannelIdentity = joinerPersona, Admission = new ArcanumAdmission { Descriptor = descriptor } }, ct);
        var hostConsecrate = host.ConsecrateAsync(fromHost, watchword, Now,
            new ConsecrateOptions { ChannelIdentity = hostPersona, Admission = new ArcanumAdmission { Descriptor = descriptor } }, ct);
        await using var joinerChannel = await joinerConsecrate;
        await using var hostChannel = await hostConsecrate;

        await joinerChannel.SendTextAsync("open but pseudonymous", Now, ct);
        var received = Assert.IsType<MessageReceived>(await hostChannel.Epistles.ReceiveAsync(ct));
        Assert.Equal(Sigil.FromSealPublicKey(joinerPersona.SealPublicKey), RiteAuthor.AuthorSigil(received.Epistle.AuthorSealPublicKey!));
        Assert.NotEqual(joiner.Identity.Sigil, RiteAuthor.AuthorSigil(received.Epistle.AuthorSealPublicKey!));
    }
}
