using System.Net;
using System.Text;
using CupriNet.Alembic;
using CupriNet.Alembic.Simulacrum;
using CupriNet.Abstractions;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

public class PairingTests
{
    private static readonly Concordium Network = new("example.chat");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static ICryptoSuite Suite() => new SimulacrumSuite(InsecureConsent.IUnderstandThisProvidesNoSecurity());

    [Fact]
    public async Task TwoNodes_PairViaIntonation_ThenExchangeApplicationMessage()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = Suite();

        var host = NodeIdentity.Generate(suite);    // the inviter / responder
        var joiner = NodeIdentity.Generate(suite);  // dials in using the link

        await using var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var port = listener.LocalEndPoint.Port;

        // Host intones a connection URL advertising its listener beacon.
        var intonation = IntonationMint.Intone(host, suite, new IntonationOptions
        {
            Network = Network,
            Beacons = [new Beacon(EndpointKind.Host, "127.0.0.1", port)],
        }, DateTimeOffset.UtcNow);
        var uri = IntonationUri.ToUri(intonation);

        // Joiner validates the URL and dials the advertised beacon.
        var validation = IntonationValidator.ValidateUri(uri, Network, suite, DateTimeOffset.UtcNow);
        Assert.True(validation.IsValid);
        var beacon = validation.Intonation!.Beacons[0];

        var acceptTask = listener.AcceptAsync(ct);
        await using var joinerVessel = await TcpVessel.ConnectAsync(beacon.Host, beacon.Port, cancellationToken: ct);
        await using var hostVessel = await acceptTask;

        // Both handshake sides run concurrently; joiner pins the host Sigil from the Intonation.
        var initiate = ConjunctionHandshake.InitiateAsync(
            joinerVessel, joiner, Network, suite, expectedPeer: intonation.InviterSigil, cancellationToken: ct);
        var accept = ConjunctionHandshake.AcceptAsync(hostVessel, host, Network, suite, cancellationToken: ct);

        var joinerView = await initiate;
        var hostView = await accept;

        Assert.Equal(host.Sigil, joinerView.PeerSigil);   // joiner authenticated the host
        Assert.Equal(joiner.Sigil, hostView.PeerSigil);   // host authenticated the joiner

        // Exchange an application-layer message on a non-control stream.
        await joinerVessel.SendAsync(5, "hello"u8.ToArray(), ct);
        var frame = await hostVessel.ReceiveAsync(ct);
        Assert.NotNull(frame);
        Assert.Equal((ushort)5, frame.Value.StreamId);
        Assert.Equal("hello", Encoding.UTF8.GetString(frame.Value.Payload));
    }

    [Fact]
    public async Task Handshake_WrongExpectedPeer_IsRejected()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = Suite();

        var host = NodeIdentity.Generate(suite);
        var joiner = NodeIdentity.Generate(suite);
        var imposter = NodeIdentity.Generate(suite).Sigil;

        await using var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        await using var joinerVessel = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        await using var hostVessel = await acceptTask;

        var initiate = ConjunctionHandshake.InitiateAsync(
            joinerVessel, joiner, Network, suite, expectedPeer: imposter, cancellationToken: ct);
        var accept = ConjunctionHandshake.AcceptAsync(hostVessel, host, Network, suite, cancellationToken: ct);

        await Assert.ThrowsAsync<ConjunctionException>(async () => await initiate);
        try { await accept; } catch { /* peer completes or faults on close; not under test */ }
    }

    [Fact]
    public async Task Handshake_NetworkMismatch_IsRejected()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = Suite();

        var host = NodeIdentity.Generate(suite);
        var joiner = NodeIdentity.Generate(suite);

        await using var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        await using var joinerVessel = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        await using var hostVessel = await acceptTask;

        var initiate = ConjunctionHandshake.InitiateAsync(joinerVessel, joiner, new Concordium("net.a"), suite, cancellationToken: ct);
        var accept = ConjunctionHandshake.AcceptAsync(hostVessel, host, new Concordium("net.b"), suite, cancellationToken: ct);

        await Assert.ThrowsAsync<ConjunctionException>(async () => await initiate);
        try { await accept; } catch { /* mismatch surfaces on at least one side */ }
    }
}
