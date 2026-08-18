using System.Net;
using System.Text;
using CupriNet.Codex;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Marks;
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

    /// <summary>Hand-rolls a frame, so a malformed one can be built deliberately. v1 is the current wire version.</summary>
    private static byte[] RawFrame(byte version, byte kind, string topic, byte[] payload)
    {
        var w = new CodexWriter();
        w.WriteByte(version);
        w.WriteByte(kind);
        w.WriteString(topic);
        w.WriteBytes(payload);
        return w.ToArray();
    }

    [Fact]
    public void Decode_RejectsOversizedPayload_FromAHostilePeer()
    {
        // A frame declaring a payload past the ceiling must be refused, not allocated.
        var raw = RawFrame(1, (byte)AuspiceFrameKind.Update, "hostile", new byte[AuspiceCodec.MaxPayloadBytes + 1]);
        Assert.Throws<CodexFormatException>(() => AuspiceCodec.Decode(raw));
    }

    [Fact]
    public void Decode_RejectsUnknownKindAndEmptyTopic()
    {
        Assert.Throws<CodexFormatException>(() => AuspiceCodec.Decode(RawFrame(1, 99, "t", [])));
        Assert.Throws<CodexFormatException>(() => AuspiceCodec.Decode(RawFrame(1, (byte)AuspiceFrameKind.Update, "", [])));
    }

    [Fact]
    public void Decode_RejectsAnUnsupportedFrameVersion()
    {
        // A peer speaking a version this build doesn't know is a typed refusal, not a mis-parse. This is what makes
        // "newer Pilgrim, older Shrine" degrade deliberately instead of hanging on an unintelligible frame.
        var future = RawFrame(99, (byte)AuspiceFrameKind.Update, "t", []);
        var ex = Assert.Throws<CodexFormatException>(() => AuspiceCodec.Decode(future));
        Assert.Contains("Auspice frame version", ex.Message);
    }

    [Fact]
    public void Encode_StampsTheCatalogueVersion()
    {
        var encoded = AuspiceCodec.Encode(new AuspiceFrame { Kind = AuspiceFrameKind.Attend, Topic = "t" });
        Assert.Equal((byte)CupriMarks.Supported(CupriMarks.Auspice).Max, encoded[0]);
    }

    [Fact]
    public void Padding_Blocks_QuantisesToTheNextMultiple()
    {
        var padding = AuspicePadding.Blocks(512);

        Assert.Equal(512, padding.Quantise(1));
        Assert.Equal(512, padding.Quantise(511));
        Assert.Equal(512, padding.Quantise(512));   // already on a boundary: no growth
        Assert.Equal(1024, padding.Quantise(513));
        Assert.Equal(0, padding.Quantise(0));
    }

    [Fact]
    public void Padding_PowersOfTwo_CollapsesTheRangeIntoBuckets()
    {
        var padding = AuspicePadding.PowersOfTwo;

        Assert.Equal(256, padding.Quantise(1));     // floored at the minimum bucket
        Assert.Equal(256, padding.Quantise(256));
        Assert.Equal(512, padding.Quantise(257));
        Assert.Equal(4096, padding.Quantise(2049));
    }

    [Fact]
    public void Padding_NeverExceedsWhatTheTransportWillCarry()
    {
        // A near-limit frame must not be padded into a message the 256 KiB transport would refuse.
        var padded = AuspicePadding.PowersOfTwo.Quantise(AuspiceCodec.MaxPayloadBytes);

        Assert.True(padded <= AuspiceCodec.MaxPaddedBytes);
        Assert.True(padded < 256 * 1024);
        Assert.True(padded >= AuspiceCodec.MaxPayloadBytes);   // and never shrinks a frame
    }

    [Fact]
    public void Padding_None_LeavesLengthsExact()
    {
        Assert.Equal(1, AuspicePadding.None.Quantise(1));
        Assert.Equal(9999, AuspicePadding.None.Quantise(9999));
    }

    [Fact]
    public void Decode_IgnoresTrailingPadding()
    {
        // Padding is appended to the encoded frame; because the payload is length-prefixed, a reader stops at the
        // real end and never sees it. Two differently-padded copies must decode identically.
        var frame = new AuspiceFrame { Kind = AuspiceFrameKind.Update, Topic = "t", Payload = "hi"u8.ToArray() };
        var encoded = AuspiceCodec.Encode(frame);

        var padded = new byte[encoded.Length + 500];
        encoded.CopyTo(padded, 0);

        var decoded = AuspiceCodec.Decode(padded);
        Assert.Equal(AuspiceFrameKind.Update, decoded.Kind);
        Assert.Equal("t", decoded.Topic);
        Assert.Equal("hi", decoded.AsText());
    }

    [Fact]
    public async Task Padding_MakesDifferentSizedUpdates_IndistinguishableOnTheWire()
    {
        // Two updates whose payloads differ in length must produce the SAME number of transport bytes once padded.
        var sizes = new List<int>();
        var channel = new RecordingChannel(sizes);
        var session = new AuspiceSession(channel, AuspicePadding.Blocks(512));

        await session.SendUpdateAsync("t", new byte[10]);
        await session.SendUpdateAsync("t", new byte[200]);

        Assert.Equal(2, sizes.Count);
        Assert.Equal(sizes[0], sizes[1]);   // both fell in the same bucket

        // Without padding they are plainly different sizes — i.e. the defence is doing something.
        var bare = new List<int>();
        var unpadded = new AuspiceSession(new RecordingChannel(bare), AuspicePadding.None);
        await unpadded.SendUpdateAsync("t", new byte[10]);
        await unpadded.SendUpdateAsync("t", new byte[200]);
        Assert.NotEqual(bare[0], bare[1]);
    }

    /// <summary>An <see cref="IStreamChannel"/> that records how many bytes each send put on the wire.</summary>
    private sealed class RecordingChannel(List<int> sizes) : IStreamChannel
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            sizes.Add(payload.Length);
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<byte[]?>(null);
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

        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(hostSide, signet, handler, ct);
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
