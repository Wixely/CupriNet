using System.Net;
using System.Threading.Channels;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// A browser arriving over a DataChannel is admitted on the same terms as a caller on a TCP port: the subnet
/// fence applies to its address, and it counts against the same per-address share of a Shrine's visits.
/// </summary>
public class WebRtcAdmissionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private sealed class FakeWebRtcTransport : IWebRtcTransport
    {
        public WebRtcEndpoint Parameters { get; } = new()
        {
            IceUfrag = "ufrag123",
            IcePassword = "passwordpasswordpassword",
            FingerprintAlgorithm = "sha-256",
            Fingerprint = new byte[32],
            Port = 40000,
        };

        public event Action<IVessel>? ChannelAccepted;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>A browser at <paramref name="browserAddress"/> opens a DataChannel; returns the browser's end.</summary>
        public DataChannelVessel Open(IPAddress browserAddress)
        {
            var toNode = Channel.CreateUnbounded<byte[]>();
            var toBrowser = Channel.CreateUnbounded<byte[]>();
            var nodeEnd = new MemoryDataChannel(toNode, toBrowser, new IPEndPoint(browserAddress, 50000));
            var browserEnd = new MemoryDataChannel(toBrowser, toNode, remote: null);
            ChannelAccepted?.Invoke(new DataChannelVessel(nodeEnd));
            return new DataChannelVessel(browserEnd);
        }
    }

    private sealed class MemoryDataChannel(Channel<byte[]> inbound, Channel<byte[]> outbound, EndPoint? remote) : IDataChannel
    {
        public EndPoint? RemoteEndPoint => remote;
        public EndPoint? LocalEndPoint => null;
        public int MaxMessageBytes => 0;

        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        {
            outbound.Writer.TryWrite(message.ToArray());
            return ValueTask.CompletedTask;
        }

        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            try { return await inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
            catch (ChannelClosedException) { return null; }
        }

        public ValueTask DisposeAsync()
        {
            outbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task BrowserFromADeniedSubnet_IsDropped_BeforeAnyHandshake()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var transport = new FakeWebRtcTransport();
        await using var node = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "webrtc.test", EnableOverlayGossip = false,
            WebRtcTransport = transport, DeniedSubnets = ["203.0.113.0/24"],
        }, ct);

        // Outside the fence: the node's first act on a channel is the Toll challenge, so a closed channel with
        // nothing on it means it was refused before any handshake work.
        await using var denied = transport.Open(IPAddress.Parse("203.0.113.5"));
        Assert.Null(await denied.ReceiveAsync(ct));

        // Inside it: the handshake begins.
        await using var allowed = transport.Open(IPAddress.Parse("198.51.100.5"));
        Assert.NotNull(await allowed.ReceiveAsync(ct));
    }

    [Fact]
    public async Task BrowserPilgrims_ShareThePerAddressVisitCap()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var transport = new FakeWebRtcTransport();
        await using var node = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "webrtc.test", EnableOverlayGossip = false,
            WebRtcTransport = transport, MaxPilgrimagesPerAddress = 1,
        }, ct);
        node.HostShrine(Signet.Generate(node.Suite), new DelegateOracleHandler(_ => OracleResponse.Text("ok")));

        var address = IPAddress.Parse("198.51.100.7");

        // The first visit is admitted and — never answering the Toll — sits holding the address's one share.
        await using var first = transport.Open(address);
        Assert.NotNull(await first.ReceiveAsync(ct));

        // A second from the same address is dropped; one from elsewhere is not.
        await using var second = transport.Open(address);
        Assert.Null(await second.ReceiveAsync(ct));

        await using var elsewhere = transport.Open(IPAddress.Parse("198.51.100.8"));
        Assert.NotNull(await elsewhere.ReceiveAsync(ct));
    }
}
