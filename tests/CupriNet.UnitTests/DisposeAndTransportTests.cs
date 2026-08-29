using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Two contracts a consumer meets at the edges: closing a session must never fail, and a transport that cannot carry
/// a rite's frames must say so when you connect rather than when you send.
/// </summary>
public class DisposeAndTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task DisposingAShrineSessionTwice_IsSafe()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);
        await using var visitor = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);

        var signet = Signet.Generate(host.Suite);
        host.HostShrine(signet, new DelegateOracleHandler(_ => OracleResponse.Text("ok")));
        var endpoint = host.ListenForPilgrims();

        var vessel = await TcpVessel.ConnectAsync(endpoint.Address.ToString(), endpoint.Port, cancellationToken: ct);
        var shrine = await visitor.PilgrimageOverVesselAsync(vessel, signet.Sigil, ct);
        await shrine.ConsultAsync(OracleRequest.Get("/"), ct);

        // A caller reaches the second call without writing one: the far side tears the session down, and their own
        // single dispose then lands on something already disposed. It must not throw while cleaning up correctly.
        await shrine.DisposeAsync();
        await shrine.DisposeAsync();
        await shrine.DisposeAsync();
    }

    [Fact]
    public async Task DisposingAMuxTwice_IsSafe()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        var listener = new VesselListener(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        listener.Start();
        var accepting = listener.AcceptAsync(ct);
        var a = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        var b = await accepting;
        await using var _l = listener;

        var mux = new VesselMux(a, ownsVessel: true);
        await mux.DisposeAsync();
        await mux.DisposeAsync();       // the CancellationTokenSource is gone by now; cancelling it must not throw

        await b.DisposeAsync();
        await b.DisposeAsync();         // and a Vessel is no different
    }

    [Fact]
    public void ATransportTooSmallForTheRites_IsRefusedWhenYouConnect()
    {
        // 64 KiB is the interoperable floor for a DataChannel, well under what the rites put on the wire. Nothing
        // below a rite fragments, so without this check the frame is simply refused later, by the transport.
        var tooSmall = new DataChannelVessel(new FixedSizeChannel(64 * 1024));
        var error = Assert.Throws<RiteTransportException>(() => RiteTransport.EnsureCarriesRiteFrames(tooSmall));

        Assert.Contains("65536", error.Message);
        Assert.Contains(RiteTransport.RequiredMessageBytes.ToString(), error.Message);
    }

    [Fact]
    public void ATransportThatFits_OrThatReportsNothing_IsAccepted()
    {
        // What a real WebRTC pairing negotiates today.
        RiteTransport.EnsureCarriesRiteFrames(new DataChannelVessel(new FixedSizeChannel(256 * 1024)));

        // 0 means "unknown or unbounded" — the check must not guess. This is the WebRTC adapter's current answer,
        // since CupriWebRTC does not surface the association's figure on the channel.
        RiteTransport.EnsureCarriesRiteFrames(new DataChannelVessel(new FixedSizeChannel(0)));
    }

    [Fact]
    public void AByteStreamVessel_IsNotSubjectToTheCheck()
    {
        // Only a transport whose message size was negotiated for it reports one; a Vessel sets its own frame size.
        var listener = new VesselListener(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        listener.Start();
        Assert.False(listener is IMessageSizeLimited);
        listener.DisposeAsync().AsTask().Wait();
    }

    private sealed class FixedSizeChannel(int max) : IDataChannel
    {
        public int MaxMessageBytes => max;
        public System.Net.EndPoint? RemoteEndPoint => null;
        public System.Net.EndPoint? LocalEndPoint => null;

        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<byte[]?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
