using System.Net;
using CupriNet.Abstractions;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

/// <summary>
/// The node-free Pilgrim entry: <see cref="Pilgrimage.OverVesselAsync"/> runs the same visit as
/// <see cref="CupriNode.PilgrimageOverVesselAsync"/> without a <see cref="CupriNode"/> on the Pilgrim side at all —
/// the shape a browser (no sockets, so no listener, so no node), a fetch-only CLI, or a test with no overlay needs.
/// </summary>
public class PilgrimageTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly Concordium Network = new("sites.example");

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
    public async Task Pilgrimage_WithoutANode_ConsultsShrineAndAttendsAuspice()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = Network.Value }, ct);
        var signet = Signet.Generate(hostNode.Suite);
        var handler = new DelegateOracleHandler(req => OracleResponse.Text($"<h1>welcome to {req.Path}</h1>", "text/html; charset=utf-8"));
        var feeds = new Dictionary<string, IAuspiceSource>
        {
            ["prices"] = new DelegateAuspiceSource(async (publisher, token) =>
            {
                await publisher.SnapshotAsync("42"u8.ToArray(), token);
                await Task.Delay(Timeout, token);   // stay open until the Pilgrim departs
            }),
        };

        var (pilgrimSide, hostSide, listener) = await ConnectAsync(ct);
        await using var _listener = listener;

        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(hostSide, signet, handler, feeds, ct);

        // No pilgrim node: just the three values the visit actually needs.
        await using var shrine = await Pilgrimage.OverVesselAsync(
            pilgrimSide, signet.Sigil, Network, hostNode.Suite, enableToll: true, ct);

        var response = await shrine.ConsultAsync(OracleRequest.Get("/index.html"), ct);
        Assert.Equal(200u, response.Status);
        Assert.Equal("<h1>welcome to /index.html</h1>", response.AsText());

        // Both rites ride the same vessel, so the live feed works off the same node-free session.
        await foreach (var frame in shrine.AttendAsync("prices", ct))
        {
            Assert.Equal(AuspiceFrameKind.Snapshot, frame.Kind);
            Assert.Equal("42", frame.AsText());
            break;
        }

        try { await hostTask; } catch { /* the host tears down once the Pilgrim drops */ }
    }

    [Fact]
    public async Task Pilgrimage_WithoutANode_WrongSignet_IsRejected()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = Network.Value }, ct);
        var signet = Signet.Generate(hostNode.Suite);
        var imposter = Signet.Generate(hostNode.Suite);
        var handler = new DelegateOracleHandler(_ => OracleResponse.Text("secret"));

        var (pilgrimSide, hostSide, listener) = await ConnectAsync(ct);
        await using var _listener = listener;

        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(hostSide, signet, handler, ct);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var shrine = await Pilgrimage.OverVesselAsync(
                pilgrimSide, imposter.Sigil, Network, hostNode.Suite, enableToll: true, ct);
        });

        try { await hostTask; } catch { /* expected: the vessel closed under it */ }
    }
}
