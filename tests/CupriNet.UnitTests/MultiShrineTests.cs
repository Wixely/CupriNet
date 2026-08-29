using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Several Shrines on one endpoint. The visitor names which it wants inside the Noise-encrypted binding, blinded, so
/// the host picks which Signet to present after the handshake rather than before it — no cleartext selector, and an
/// endpoint that does not host the target learns only that.
/// </summary>
public class MultiShrineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static Task<CupriNode> NodeAsync(CancellationToken ct)
        => CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);

    private static async Task<ShrineSession> VisitAsync(
        CupriNode visitor, System.Net.IPEndPoint endpoint, Signet signet, CancellationToken ct)
    {
        var vessel = await TcpVessel.ConnectAsync(endpoint.Address.ToString(), endpoint.Port, cancellationToken: ct);
        return await visitor.PilgrimageOverVesselAsync(vessel, signet.Sigil, ct);
    }

    [Fact]
    public async Task OneEndpoint_ServesSeveralSites_EachAsItself()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await NodeAsync(ct);
        await using var visitor = await NodeAsync(ct);

        var alpha = Signet.Generate(host.Suite);
        var beta = Signet.Generate(host.Suite);
        host.HostShrine(alpha, new DelegateOracleHandler(_ => OracleResponse.Text("alpha")));
        host.HostShrine(beta, new DelegateOracleHandler(_ => OracleResponse.Text("beta")));

        Assert.Equal([alpha.Address, beta.Address], host.ShrineAddresses);

        var endpoint = host.ListenForPilgrims();

        await using (var first = await VisitAsync(visitor, endpoint, alpha, ct))
            Assert.Equal("alpha", (await first.ConsultAsync(OracleRequest.Get("/"), ct)).AsText());

        await using (var second = await VisitAsync(visitor, endpoint, beta, ct))
            Assert.Equal("beta", (await second.ConsultAsync(OracleRequest.Get("/"), ct)).AsText());
    }

    [Fact]
    public async Task ASiteThisEndpointDoesNotHost_IsRefused_NotAnsweredByAnother()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await NodeAsync(ct);
        await using var visitor = await NodeAsync(ct);

        host.HostShrine(Signet.Generate(host.Suite), new DelegateOracleHandler(_ => OracleResponse.Text("mine")));
        host.HostShrine(Signet.Generate(host.Suite), new DelegateOracleHandler(_ => OracleResponse.Text("also mine")));
        var endpoint = host.ListenForPilgrims();

        // A Signet this endpoint has never heard of. The dangerous outcome would be being served one of its own
        // sites anyway; the useless outcome would be silence. It must be a refusal the visitor can read.
        var elsewhere = Signet.Generate(host.Suite);
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var _ = await VisitAsync(visitor, endpoint, elsewhere, ct);
        });

        Assert.Contains("Shrine", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AHostPublishesSeveralSites_WithOneLinkEach()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await NodeAsync(ct);
        var alpha = Signet.Generate(host.Suite);
        var beta = Signet.Generate(host.Suite);
        host.HostShrine(alpha, new DelegateOracleHandler(_ => OracleResponse.Text("alpha")));
        host.HostShrine(beta, new DelegateOracleHandler(_ => OracleResponse.Text("beta")));

        // An Intonation carries one Shrine address, so several sites are published as several links — which also
        // means only someone holding BOTH can tell they are co-hosted.
        var now = DateTimeOffset.UtcNow;
        var forAlpha = host.Intone(TimeSpan.FromHours(1), now, alpha);
        var forBeta = host.Intone(TimeSpan.FromHours(1), now, beta);

        Assert.Equal(alpha.Sigil, forAlpha.Shrine);
        Assert.Equal(beta.Sigil, forBeta.Shrine);
        Assert.NotEqual(forAlpha.Shrine, forBeta.Shrine);

        // Both are still this node's links: same inviter, so the correlation is available to a holder of both — which
        // is the trade a host accepts by advertising at all, and the reason a site meant to be unlinkable from the
        // node is shared out-of-band instead.
        Assert.Equal(forAlpha.InviterSealPublicKey, forBeta.InviterSealPublicKey);
    }

    [Fact]
    public async Task ALinkCannotAdvertiseASiteTheNodeDoesNotServe()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await NodeAsync(ct);
        host.HostShrine(Signet.Generate(host.Suite), new DelegateOracleHandler(_ => OracleResponse.Text("mine")));

        var elsewhere = Signet.Generate(host.Suite);
        var error = Assert.Throws<InvalidOperationException>(
            () => host.Intone(TimeSpan.FromHours(1), DateTimeOffset.UtcNow, elsewhere));

        Assert.Contains(elsewhere.Address, error.Message);
    }

    [Fact]
    public async Task WithNoShrineNamed_TheAdvertiseFlagStillDecides()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await NodeAsync(ct);
        var quiet = Signet.Generate(host.Suite);
        var public_ = Signet.Generate(host.Suite);
        host.HostShrine(quiet, new DelegateOracleHandler(_ => OracleResponse.Text("quiet")));
        host.HostShrine(public_, new DelegateOracleHandler(_ => OracleResponse.Text("public")), feeds: null,
            relics: null, conduit: null, advertiseInLink: true);

        // The default mint carries the flagged one, not merely the first hosted.
        var link = host.Intone(TimeSpan.FromHours(1), DateTimeOffset.UtcNow);
        Assert.Equal(public_.Sigil, link.Shrine);

        // And the unflagged site can still be published deliberately, one link of its own.
        var quietLink = host.Intone(TimeSpan.FromHours(1), DateTimeOffset.UtcNow, quiet);
        Assert.Equal(quiet.Sigil, quietLink.Shrine);
    }

    [Fact]
    public void TheTargetTag_IsBlinded_AndPerSession()
    {
        var suite = CryptoSuites.Secure();
        var signet = Signet.Generate(suite);
        var other = Signet.Generate(suite);

        var sessionA = new byte[32];
        var sessionB = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(sessionA);
        System.Security.Cryptography.RandomNumberGenerator.Fill(sessionB);

        var inA = NoiseConjunction.ShrineTargetTag(suite, sessionA, signet.Sigil);

        // Same site, same session: a host can recognise its own.
        Assert.Equal(inA, NoiseConjunction.ShrineTargetTag(suite, sessionA, signet.Sigil));

        // Same site, different session: no stable handle to link two visits by.
        Assert.NotEqual(inA, NoiseConjunction.ShrineTargetTag(suite, sessionB, signet.Sigil));

        // Different site, same session: distinguishable, so a host can tell which of its own was asked for.
        Assert.NotEqual(inA, NoiseConjunction.ShrineTargetTag(suite, sessionA, other.Sigil));

        // And it reveals nothing of the address itself.
        Assert.NotEqual(signet.Sigil.Span.ToArray(), inA);
        Assert.Equal(32, inA.Length);
    }

    [Fact]
    public async Task HostingTheSameSignetAgain_ReplacesIt_RatherThanAddingATwin()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await NodeAsync(ct);
        await using var visitor = await NodeAsync(ct);

        var signet = Signet.Generate(host.Suite);
        host.HostShrine(signet, new DelegateOracleHandler(_ => OracleResponse.Text("first")));
        host.HostShrine(signet, new DelegateOracleHandler(_ => OracleResponse.Text("second")));

        Assert.Single(host.ShrineAddresses);

        var endpoint = host.ListenForPilgrims();
        await using var shrine = await VisitAsync(visitor, endpoint, signet, ct);
        Assert.Equal("second", (await shrine.ConsultAsync(OracleRequest.Get("/"), ct)).AsText());
    }

    [Fact]
    public async Task EachSiteKeepsItsOwnRites()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await NodeAsync(ct);
        await using var visitor = await NodeAsync(ct);

        var withFeed = Signet.Generate(host.Suite);
        var withoutFeed = Signet.Generate(host.Suite);

        host.HostShrine(withFeed, new DelegateOracleHandler(_ => OracleResponse.Text("has feed")),
            new Dictionary<string, IAuspiceSource>
            {
                ["news"] = new DelegateAuspiceSource(async (publisher, token) =>
                {
                    await publisher.SnapshotAsync("hello"u8.ToArray(), token);
                    await Task.Delay(System.Threading.Timeout.Infinite, token);
                }),
            });
        host.HostShrine(withoutFeed, new DelegateOracleHandler(_ => OracleResponse.Text("no feed")));

        var endpoint = host.ListenForPilgrims();

        await using (var a = await VisitAsync(visitor, endpoint, withFeed, ct))
        {
            await foreach (var frame in a.AttendAsync("news", ct))
            {
                Assert.Equal("hello"u8.ToArray(), frame.Payload);
                break;
            }
        }

        // The other site hosts no such feed, so attending it is sealed rather than served from its neighbour.
        await using var b = await VisitAsync(visitor, endpoint, withoutFeed, ct);
        var frames = new List<AuspiceFrame>();
        await foreach (var frame in b.AttendAsync("news", ct))
            frames.Add(frame);

        Assert.Empty(frames);       // sealed immediately: "no such feed"
        Assert.Equal("no feed", (await b.ConsultAsync(OracleRequest.Get("/"), ct)).AsText());
    }

    [Fact]
    public async Task PinningOneSite_WhileTheEndpointHostsAnother_StillFailsTheHandshake()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await NodeAsync(ct);
        await using var visitor = await NodeAsync(ct);

        var real = Signet.Generate(host.Suite);
        host.HostShrine(real, new DelegateOracleHandler(_ => OracleResponse.Text("real")));
        var endpoint = host.ListenForPilgrims();

        // An impostor Signet must not be answered by the site that IS hosted — key pinning still decides.
        var impostor = Signet.Generate(host.Suite);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var _ = await VisitAsync(visitor, endpoint, impostor, ct);
        });

        // And the endpoint keeps serving the real one afterwards.
        await using var shrine = await VisitAsync(visitor, endpoint, real, ct);
        Assert.Equal("real", (await shrine.ConsultAsync(OracleRequest.Get("/"), ct)).AsText());
    }
}
