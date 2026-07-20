using System.Net;
using CupriNet.Alembic;
using CupriNet.Concordance;
using CupriNet.Core;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

public class PeerExchangeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static PeerRecord Record(ICryptoSuite suite, string ip, ulong seq = 1)
    {
        var identity = NodeIdentity.Generate(suite);
        return PeerRecordSigner.Create(identity, [new Beacon(EndpointKind.Host, ip, 43820)], seq, PeerCapabilities.None, suite, Now);
    }

    private static async Task<(VesselSession Client, VesselSession Server, VesselListener Listener)> ConnectedPairAsync(CancellationToken ct)
    {
        var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        var client = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        var server = await acceptTask;
        return (client, server, listener);
    }

    [Fact]
    public async Task Pull_LearnsServersKnownPeers()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();

        // Server knows three peers in distinct /24s.
        var serverView = new Constellation();
        var peers = new[] { Record(suite, "10.0.0.5"), Record(suite, "10.0.1.5"), Record(suite, "10.0.2.5") };
        foreach (var p in peers)
            serverView.Admit(p, PeerBucket.Wayfarers, Now);

        var clientView = new Constellation();

        var (client, server, listener) = await ConnectedPairAsync(ct);
        await using var _c = client;
        await using var _s = server;
        await using var _l = listener;

        var serveTask = PeerExchange.ServeOnceAsync(server, serverView, cancellationToken: ct);
        var outcome = await PeerExchange.PullAsync(client, clientView, suite, Now, cancellationToken: ct);
        await serveTask;

        Assert.Equal(3, outcome.Admitted);
        Assert.Equal(0, outcome.Invalid);
        Assert.Equal(3, clientView.Count);
        foreach (var p in peers)
            Assert.NotNull(clientView.Get(p.Sigil));
    }

    [Fact]
    public void AdmitRecords_DropsInvalidSignatures()
    {
        var suite = CryptoSuites.Secure();
        var genuine = Record(suite, "10.0.0.5");
        var tampered = genuine with { SequenceNumber = genuine.SequenceNumber + 1 }; // signature no longer matches

        var view = new Constellation();
        var outcome = PeerExchange.AdmitRecords(view, [genuine, tampered], suite, Now, PeerBucket.Strangers, source: "peer");

        Assert.Equal(1, outcome.Admitted);
        Assert.Equal(1, outcome.Invalid);
        Assert.Equal(1, view.Count);
    }

    [Fact]
    public async Task Serve_CapsSampleAtMaxPerExchange()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Simulacrum();

        // Server knows more than the per-exchange cap, each in a distinct /24 so all are eligible.
        var serverView = new Constellation(new ConstellationOptions { MaxPerSlash24 = 1, MaxRecords = 1000 });
        for (var i = 0; i < PeerExchange.MaxRecordsPerExchange + 8; i++)
            serverView.Admit(Record(suite, $"10.0.{i}.5"), PeerBucket.Wayfarers, Now);

        var clientView = new Constellation(new ConstellationOptions { MaxRecords = 1000 });

        var (client, server, listener) = await ConnectedPairAsync(ct);
        await using var _c = client;
        await using var _s = server;
        await using var _l = listener;

        var serveTask = PeerExchange.ServeOnceAsync(server, serverView, cancellationToken: ct);
        await PeerExchange.RequestAsync(client, maxRequested: 1000, cancellationToken: ct);
        var records = await PeerExchange.ReadSampleAsync(client, cancellationToken: ct);
        await serveTask;

        Assert.Equal(PeerExchange.MaxRecordsPerExchange, records.Count);
    }
}
