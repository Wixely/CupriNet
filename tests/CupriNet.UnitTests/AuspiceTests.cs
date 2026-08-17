using System.Net;
using System.Text;
using CupriNet.Codex;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

/// <summary>
/// The Auspice rite: subscribe-once, push-many live feeds beside the Oracle's request/response. Covers the codec, the
/// payload ceiling that keeps a message inside what a browser DataChannel will carry, and an end-to-end attendance
/// running concurrently with an Oracle fetch over one Pilgrimage.
/// </summary>
public class AuspiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void Frame_Codec_RoundTrips()
    {
        var frame = new AuspiceFrame
        {
            Kind = AuspiceFrameKind.Update,
            Topic = "constellation",
            Payload = "{\"peers\":3}"u8.ToArray(),
        };

        var decoded = AuspiceCodec.Decode(AuspiceCodec.Encode(frame));

        Assert.Equal(AuspiceFrameKind.Update, decoded.Kind);
        Assert.Equal("constellation", decoded.Topic);
        Assert.Equal("{\"peers\":3}", decoded.AsText());
    }

    [Fact]
    public void Encode_RejectsOversizedPayload_BeforeAnyIo()
    {
        var frame = new AuspiceFrame
        {
            Kind = AuspiceFrameKind.Snapshot,
            Topic = "big",
            Payload = new byte[AuspiceCodec.MaxPayloadBytes + 1],
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => AuspiceCodec.Encode(frame));
        Assert.Contains("Reliquary", ex.Message);   // points at the right tool for bulk content
    }

    [Fact]
    public void Encode_AcceptsExactlyTheLimit()
    {
        var frame = new AuspiceFrame
        {
            Kind = AuspiceFrameKind.Snapshot,
            Topic = "atlimit",
            Payload = new byte[AuspiceCodec.MaxPayloadBytes],
        };

        var decoded = AuspiceCodec.Decode(AuspiceCodec.Encode(frame));
        Assert.Equal(AuspiceCodec.MaxPayloadBytes, decoded.Payload.Length);
    }

    [Fact]
    public void PayloadCeiling_LeavesHeadroomUnderTheBrowserDataChannelLimit()
    {
        // The transport ceiling is CupriWebRTC's SctpAssociation.MaxMessageBytes (256 KiB), which is what an endpoint
        // may honestly advertise as SDP a=max-message-size. Our cap must sit below it with room for framing.
        const int browserDataChannelLimit = 256 * 1024;
        Assert.True(AuspiceCodec.MaxPayloadBytes < browserDataChannelLimit);
        Assert.True(browserDataChannelLimit - AuspiceCodec.MaxPayloadBytes >= 32 * 1024);
    }

    [Fact]
    public void Decode_RejectsOversizedPayload_FromAHostilePeer()
    {
        // Hand-roll a frame that declares a payload past the ceiling: the decoder must refuse it, not allocate it.
        var w = new CodexWriter();
        w.WriteByte((byte)AuspiceFrameKind.Update);
        w.WriteString("hostile");
        w.WriteBytes(new byte[AuspiceCodec.MaxPayloadBytes + 1]);

        Assert.Throws<CodexFormatException>(() => AuspiceCodec.Decode(w.ToArray()));
    }

    [Fact]
    public void Decode_RejectsUnknownKindAndEmptyTopic()
    {
        var badKind = new CodexWriter();
        badKind.WriteByte(99);
        badKind.WriteString("t");
        badKind.WriteBytes([]);
        Assert.Throws<CodexFormatException>(() => AuspiceCodec.Decode(badKind.ToArray()));

        var noTopic = new CodexWriter();
        noTopic.WriteByte((byte)AuspiceFrameKind.Update);
        noTopic.WriteString("");
        noTopic.WriteBytes([]);
        Assert.Throws<CodexFormatException>(() => AuspiceCodec.Decode(noTopic.ToArray()));
    }

    [Fact]
    public async Task Attend_ReceivesSnapshotThenUpdates_AlongsideAnOracleFetch()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);
        await using var pilgrimNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);

        var signet = Signet.Generate(hostNode.Suite);
        var handler = new DelegateOracleHandler(_ => OracleResponse.Text("<h1>page</h1>", "text/html; charset=utf-8"));

        // A feed that emanates a snapshot then three deltas.
        var feeds = new Dictionary<string, IAuspiceSource>
        {
            ["ticks"] = new DelegateAuspiceSource(async (publisher, token) =>
            {
                await publisher.SnapshotAsync("snapshot"u8.ToArray(), token);
                for (var i = 1; i <= 3; i++)
                    await publisher.UpdateAsync(Encoding.UTF8.GetBytes($"tick {i}"), token);
                await Task.Delay(Timeout, token);   // stay open until the Pilgrim departs
            }),
        };

        var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        VesselSession pilgrimSide = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        VesselSession hostSide = await acceptTask;
        await using var _listener = listener;

        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(hostSide, signet, handler, feeds, ct);
        await using var shrine = await pilgrimNode.PilgrimageOverVesselAsync(pilgrimSide, signet.Sigil, ct);

        // The live feed and the page fetch share one connection.
        var received = new List<AuspiceFrame>();
        await foreach (var frame in shrine.AttendAsync("ticks", ct))
        {
            received.Add(frame);
            if (received.Count == 1)
            {
                // Mid-stream, fetch a page over the Oracle to prove the two rites are demultiplexed.
                var page = await shrine.ConsultAsync(OracleRequest.Get("/index.html"), ct);
                Assert.Equal(200u, page.Status);
                Assert.Equal("<h1>page</h1>", page.AsText());
            }
            if (received.Count == 4)
                break;
        }

        Assert.Equal(AuspiceFrameKind.Snapshot, received[0].Kind);
        Assert.Equal("snapshot", received[0].AsText());
        Assert.All(received.Skip(1), f => Assert.Equal(AuspiceFrameKind.Update, f.Kind));
        Assert.Equal(["tick 1", "tick 2", "tick 3"], received.Skip(1).Select(f => f.AsText()));
    }

    [Fact]
    public async Task Attend_UnknownFeed_IsSealedNotIgnored()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);
        await using var pilgrimNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);

        var signet = Signet.Generate(hostNode.Suite);
        var handler = new DelegateOracleHandler(_ => OracleResponse.NotFound());

        var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        VesselSession pilgrimSide = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        VesselSession hostSide = await acceptTask;
        await using var _listener = listener;

        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(hostSide, signet, handler, cancellationToken: ct);
        await using var shrine = await pilgrimNode.PilgrimageOverVesselAsync(pilgrimSide, signet.Sigil, ct);

        // The host hosts no feeds at all, so attending one must terminate rather than hang.
        await shrine.Auspices.AttendAsync("nope", ct);
        var frame = await shrine.Auspices.ReceiveAsync(ct);

        Assert.NotNull(frame);
        Assert.Equal(AuspiceFrameKind.Sealed, frame!.Kind);
        Assert.Equal("nope", frame.Topic);
        Assert.Equal("no such feed", frame.AsText());
    }
}
