using System.Net;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

/// <summary>
/// The Pilgrimage: a Pilgrim connects to a Shrine (an L2 site) over a direct vessel, pins the Shrine's Signet
/// (self-authenticating, .onion-style), and consults its Oracle rite — with no overlay join. Two real CupriNode
/// instances over a loopback vessel pair.
/// </summary>
public class CupriNodeShrineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static async Task<(VesselSession pilgrimSide, VesselSession hostSide, VesselListener listener)> ConnectAsync(CancellationToken ct)
    {
        var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        var pilgrimSide = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        var hostSide = await acceptTask;
        return (pilgrimSide, hostSide, listener);
    }

    [Fact]
    public async Task Pilgrimage_ConsultsShrine_OverEncryptedSession()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);
        await using var pilgrimNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);

        var signet = Signet.Generate(hostNode.Suite);
        var handler = new DelegateOracleHandler(req => OracleResponse.Text($"<h1>welcome to {req.Path}</h1>", "text/html; charset=utf-8"));

        var (pilgrimSide, hostSide, listener) = await ConnectAsync(ct);
        await using var _listener = listener;

        // Host serves a Pilgrimage over its side of the vessel, presenting only the Signet.
        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(hostSide, signet, handler, ct);

        // Pilgrim dials, pinning the Signet (the site URL is signet.Address).
        await using var shrine = await pilgrimNode.PilgrimageOverVesselAsync(pilgrimSide, signet.Sigil, ct);
        var response = await shrine.ConsultAsync(OracleRequest.Get("/index.html"), ct);

        Assert.Equal(200u, response.Status);
        Assert.Equal("text/html; charset=utf-8", response.ContentType);
        Assert.Equal("<h1>welcome to /index.html</h1>", response.AsText());
    }

    [Fact]
    public async Task Pilgrimage_WrongSignet_IsRejected()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);
        await using var pilgrimNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);

        var signet = Signet.Generate(hostNode.Suite);
        var imposter = Signet.Generate(hostNode.Suite);
        var handler = new DelegateOracleHandler(_ => OracleResponse.Text("secret"));

        var (pilgrimSide, hostSide, listener) = await ConnectAsync(ct);
        await using var _listener = listener;

        // Host presents `signet`, but the Pilgrim pins a DIFFERENT Signet — the handshake must fail for the Pilgrim.
        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(hostSide, signet, handler, ct);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var shrine = await pilgrimNode.PilgrimageOverVesselAsync(pilgrimSide, imposter.Sigil, ct);
        });

        // The host side tears down once the Pilgrim drops; don't assert its outcome, just observe it.
        try { await hostTask; } catch { /* expected: the vessel closed under it */ }
    }
}
