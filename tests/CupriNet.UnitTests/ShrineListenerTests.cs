using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// The Shrine listener: a port of its own on which every connection is a Pilgrimage and the host presents only the
/// Signet. It exists because the identity to present must be chosen before the Noise handshake, so the L1 port — which
/// presents the node's Sigil — cannot serve a Shrine without a pre-Noise cleartext selector the design refuses.
/// </summary>
public class ShrineListenerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static Task<CupriNode> NodeAsync(CancellationToken ct)
        => CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);

    [Fact]
    public async Task APilgrimDialsThePort_AndConsultsTheShrine()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await NodeAsync(ct);
        await using var visitor = await NodeAsync(ct);

        var signet = Signet.Generate(host.Suite);
        host.HostShrine(signet, new DelegateOracleHandler(req => OracleResponse.Text($"page:{req.Path}")));

        var endpoint = host.ListenForPilgrims();
        Assert.Equal(endpoint, host.ShrineEndPoint);
        Assert.NotEqual(0, endpoint.Port);

        // No listener loop of the caller's own: dial the port, pin the address, fetch.
        var vessel = await TcpVessel.ConnectAsync(
            endpoint.Address.ToString(), endpoint.Port, cancellationToken: ct);
        await using var shrine = await visitor.PilgrimageOverVesselAsync(vessel, signet.Sigil, ct);

        var page = await shrine.ConsultAsync(OracleRequest.Get("/index.html"), ct);
        Assert.Equal(200u, page.Status);
        Assert.Equal("page:/index.html", page.AsText());
    }

    [Fact]
    public async Task ThePortServesEveryRite_NotJustTheOracle()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await NodeAsync(ct);
        await using var visitor = await NodeAsync(ct);

        var signet = Signet.Generate(host.Suite);
        var feeds = new Dictionary<string, IAuspiceSource>
        {
            ["ticks"] = new DelegateAuspiceSource(async (publisher, token) =>
            {
                await publisher.SnapshotAsync("snapshot"u8.ToArray(), token);
                await publisher.UpdateAsync("update"u8.ToArray(), token);
                await Task.Delay(System.Threading.Timeout.Infinite, token);
            }),
        };
        var conduit = new DelegateConduitHandler(async (session, token) =>
        {
            var frame = await session.ReceiveAsync(token);
            if (frame is not null)
                await session.SendAsync(frame with { Payload = "pong"u8.ToArray() }, token);
            await Task.Delay(System.Threading.Timeout.Infinite, token);
        });

        host.HostShrine(signet, new DelegateOracleHandler(_ => OracleResponse.Text("ok")), feeds, relics: null, conduit);
        var endpoint = host.ListenForPilgrims();

        var vessel = await TcpVessel.ConnectAsync(endpoint.Address.ToString(), endpoint.Port, cancellationToken: ct);
        await using var shrine = await visitor.PilgrimageOverVesselAsync(vessel, signet.Sigil, ct);

        await shrine.Conduits.SendAsync(
            new ConduitFrame { ProtocolId = 1, SchemaVersion = 1, Flags = 0, Payload = "ping"u8.ToArray() }, ct);

        Assert.Equal("ok", (await shrine.ConsultAsync(OracleRequest.Get("/"), ct)).AsText());

        var reply = await shrine.Conduits.ReceiveAsync(ct);
        Assert.NotNull(reply);
        Assert.Equal("pong"u8.ToArray(), reply.Payload);

        var frames = new List<AuspiceFrame>();
        await foreach (var frame in shrine.AttendAsync("ticks", ct))
        {
            frames.Add(frame);
            if (frames.Count == 2)
                break;
        }

        Assert.Equal(AuspiceFrameKind.Snapshot, frames[0].Kind);
        Assert.Equal(AuspiceFrameKind.Update, frames[1].Kind);
    }

    [Fact]
    public async Task PinningTheNodesSigil_Fails_RatherThanSucceedingIntoNothing()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await NodeAsync(ct);
        await using var visitor = await NodeAsync(ct);

        var signet = Signet.Generate(host.Suite);
        host.HostShrine(signet, new DelegateOracleHandler(_ => OracleResponse.Text("secret")));
        var endpoint = host.ListenForPilgrims();

        // The whole point of the dedicated port. On the L1 port this SUCCEEDS, into a healthy session with no Shrine
        // behind it and every rite silent — the failure that reads as a broken rite. Here the host presents only the
        // Signet, so pinning the node's Sigil cannot complete, and the mistake is loud at the handshake.
        var vessel = await TcpVessel.ConnectAsync(endpoint.Address.ToString(), endpoint.Port, cancellationToken: ct);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var wrong = await visitor.PilgrimageOverVesselAsync(vessel, host.Identity.Sigil, ct);
        });
    }

    [Fact]
    public async Task ListeningWithoutAShrine_IsRefusedAtTheCall()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var node = await NodeAsync(ct);

        var error = Assert.Throws<InvalidOperationException>(() => node.ListenForPilgrims());
        Assert.Contains("HostShrine", error.Message);
        Assert.Null(node.ShrineEndPoint);
    }

    [Fact]
    public async Task ListeningTwice_IsRefused()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var node = await NodeAsync(ct);
        node.HostShrine(Signet.Generate(node.Suite), new DelegateOracleHandler(_ => OracleResponse.Text("ok")));

        var first = node.ListenForPilgrims();
        var error = Assert.Throws<InvalidOperationException>(() => node.ListenForPilgrims());
        Assert.Contains(first.Port.ToString(), error.Message);
    }

    [Fact]
    public async Task SeveralPilgrimsAreServedConcurrently()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await NodeAsync(ct);
        var signet = Signet.Generate(host.Suite);
        host.HostShrine(signet, new DelegateOracleHandler(req => OracleResponse.Text($"page:{req.Path}")));
        var endpoint = host.ListenForPilgrims();

        // A Shrine bounds visitors, not handshakes, so a second visit must not wait on the first finishing.
        var visits = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
        {
            await using var visitor = await NodeAsync(ct);
            var vessel = await TcpVessel.ConnectAsync(endpoint.Address.ToString(), endpoint.Port, cancellationToken: ct);
            await using var shrine = await visitor.PilgrimageOverVesselAsync(vessel, signet.Sigil, ct);
            return (await shrine.ConsultAsync(OracleRequest.Get($"/{i}"), ct)).AsText();
        }));

        Assert.Equal(4, visits.Distinct().Count());
    }

    [Fact]
    public async Task AnIdleVisit_IsClosed_SoItCannotHoldItsSlotForever()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        // The attack this closes: complete the handshake, then say nothing, and hold a visit slot indefinitely.
        await using var host = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "sites.example",
            PilgrimageIdleTimeout = TimeSpan.FromSeconds(2),
        }, ct);
        await using var visitor = await NodeAsync(ct);

        var signet = Signet.Generate(host.Suite);
        host.HostShrine(signet, new DelegateOracleHandler(_ => OracleResponse.Text("ok")));
        var endpoint = host.ListenForPilgrims();

        var vessel = await TcpVessel.ConnectAsync(endpoint.Address.ToString(), endpoint.Port, cancellationToken: ct);
        await using var shrine = await visitor.PilgrimageOverVesselAsync(vessel, signet.Sigil, ct);

        // It works while it is being used...
        Assert.Equal("ok", (await shrine.ConsultAsync(OracleRequest.Get("/"), ct)).AsText());

        // ...and the host hangs up once it goes quiet. The Oracle stream closing is how that surfaces here.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        await Assert.ThrowsAnyAsync<Exception>(() => shrine.ConsultAsync(OracleRequest.Get("/"), ct));
    }

    [Fact]
    public async Task AttendingAFeed_KeepsAVisitAlive_ThoughThePilgrimSendsNothingMore()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        // The false positive an inbound-only deadline would cause: a Pilgrim attends a feed, sends nothing further,
        // and listens. That is the Auspice working as designed, not an idle connection.
        await using var host = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "sites.example",
            PilgrimageIdleTimeout = TimeSpan.FromSeconds(2),
        }, ct);
        await using var visitor = await NodeAsync(ct);

        var signet = Signet.Generate(host.Suite);
        host.HostShrine(signet, new DelegateOracleHandler(_ => OracleResponse.Text("ok")),
            new Dictionary<string, IAuspiceSource>
            {
                ["ticks"] = new DelegateAuspiceSource(async (publisher, token) =>
                {
                    await publisher.SnapshotAsync("start"u8.ToArray(), token);
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(400), token);
                        await publisher.UpdateAsync("tick"u8.ToArray(), token);
                    }
                }),
            });
        var endpoint = host.ListenForPilgrims();

        var vessel = await TcpVessel.ConnectAsync(endpoint.Address.ToString(), endpoint.Port, cancellationToken: ct);
        await using var shrine = await visitor.PilgrimageOverVesselAsync(vessel, signet.Sigil, ct);

        var seen = 0;
        await foreach (var frame in shrine.AttendAsync("ticks", ct))
        {
            if (++seen >= 12)                       // ~5s of updates, well past the 2s deadline
                break;
        }

        Assert.Equal(12, seen);
    }

    [Fact]
    public async Task OneAddress_CannotTakeEveryVisitSlot()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "sites.example",
            MaxPilgrimagesPerAddress = 2,
            PilgrimageIdleTimeout = TimeSpan.Zero,       // isolate the cap from the deadline
        }, ct);
        await using var visitor = await NodeAsync(ct);

        var signet = Signet.Generate(host.Suite);
        host.HostShrine(signet, new DelegateOracleHandler(_ => OracleResponse.Text("ok")));
        var endpoint = host.ListenForPilgrims();

        // Two concurrent visits from this address are fine.
        var first = await VisitAsync(visitor, endpoint, signet, ct);
        var second = await VisitAsync(visitor, endpoint, signet, ct);
        Assert.Equal("ok", (await first.ConsultAsync(OracleRequest.Get("/"), ct)).AsText());
        Assert.Equal("ok", (await second.ConsultAsync(OracleRequest.Get("/"), ct)).AsText());

        // The third is dropped before the handshake, so it cannot hold a slot.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var _ = await VisitAsync(visitor, endpoint, signet, ct);
        });

        // Releasing one frees the budget again — the cap is concurrent, not cumulative.
        await first.DisposeAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        await using var third = await VisitAsync(visitor, endpoint, signet, ct);
        Assert.Equal("ok", (await third.ConsultAsync(OracleRequest.Get("/"), ct)).AsText());

        await second.DisposeAsync();
    }

    private static async Task<ShrineSession> VisitAsync(
        CupriNode visitor, System.Net.IPEndPoint endpoint, Signet signet, CancellationToken ct)
    {
        var vessel = await TcpVessel.ConnectAsync(endpoint.Address.ToString(), endpoint.Port, cancellationToken: ct);
        return await visitor.PilgrimageOverVesselAsync(vessel, signet.Sigil, ct);
    }

    [Fact]
    public async Task DisposingTheNode_ClosesThePort()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        var node = await NodeAsync(ct);
        node.HostShrine(Signet.Generate(node.Suite), new DelegateOracleHandler(_ => OracleResponse.Text("ok")));
        var endpoint = node.ListenForPilgrims();
        await node.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            TcpVessel.ConnectAsync(endpoint.Address.ToString(), endpoint.Port, cancellationToken: ct));
    }
}
