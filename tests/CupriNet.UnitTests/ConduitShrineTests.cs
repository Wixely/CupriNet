using System.Net;
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
/// The Conduit rite reached from a Pilgrimage: a raw duplex session beside the Oracle, the Auspice and the Relic, for
/// an application protocol that is neither request/response nor snapshot-then-updates. Covers the Pilgrimage-side
/// guarantees the Arcanum-side conduit deliberately does not carry — the browser payload ceiling, padding, the
/// CupriMark version byte, and being told when a Shrine hosts no conduit.
/// </summary>
public class ConduitShrineTests
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

    private static OracleResponse NoPage(OracleRequest _) => OracleResponse.Text("no page here");

    [Fact]
    public async Task Pilgrimage_DrivesAConduit_BothWays()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);
        await using var pilgrimNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);

        var signet = Signet.Generate(hostNode.Suite);

        // An echo protocol: exactly the shape an existing framed protocol meets the seam with.
        var conduit = new DelegateConduitHandler(async (session, token) =>
        {
            while (true)
            {
                var frame = await session.ReceiveAsync(token);
                if (frame is null || frame.IsSealed)
                    return;

                await session.SendAsync(frame with { Payload = [.. frame.Payload, .. "-pong"u8] }, token);
            }
        });

        var (pilgrimSide, hostSide, listener) = await ConnectAsync(ct);
        await using var _listener = listener;

        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(
            hostSide, signet, new DelegateOracleHandler(NoPage), feeds: null, relics: null, conduit, ct);

        await using var shrine = await pilgrimNode.PilgrimageOverVesselAsync(pilgrimSide, signet.Sigil, ct);

        for (var i = 0; i < 3; i++)
        {
            await shrine.Conduits.SendAsync(
                new ConduitFrame { ProtocolId = 7, SchemaVersion = 1, Flags = 0, Payload = [(byte)i] }, ct);

            var reply = await shrine.Conduits.ReceiveAsync(ct);
            Assert.NotNull(reply);
            Assert.Equal(7u, reply.ProtocolId);
            Assert.Equal((byte[])[(byte)i, .. "-pong"u8], reply.Payload);
        }
    }

    [Fact]
    public async Task Pilgrimage_RunsAConduit_AlongsideTheOracle()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);
        await using var pilgrimNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);

        var signet = Signet.Generate(hostNode.Suite);
        var conduit = new DelegateConduitHandler(async (session, token) =>
        {
            var frame = await session.ReceiveAsync(token);
            if (frame is not null)
                await session.SendAsync(frame with { Payload = "session-reply"u8.ToArray() }, token);

            await Task.Delay(System.Threading.Timeout.Infinite, token);   // hold the conduit open
        });

        var (pilgrimSide, hostSide, listener) = await ConnectAsync(ct);
        await using var _listener = listener;

        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(
            hostSide, signet, new DelegateOracleHandler(req => OracleResponse.Text($"page:{req.Path}")), null, null, conduit, ct);

        await using var shrine = await pilgrimNode.PilgrimageOverVesselAsync(pilgrimSide, signet.Sigil, ct);

        // The whole point of the mux: a page fetch and a live session share one visit.
        await shrine.Conduits.SendAsync(
            new ConduitFrame { ProtocolId = 1, SchemaVersion = 1, Flags = 0, Payload = "hello"u8.ToArray() }, ct);

        var page = await shrine.ConsultAsync(OracleRequest.Get("/index.html"), ct);
        var reply = await shrine.Conduits.ReceiveAsync(ct);

        Assert.Equal("page:/index.html", page.AsText());
        Assert.NotNull(reply);
        Assert.Equal("session-reply"u8.ToArray(), reply.Payload);
    }

    [Fact]
    public async Task Shrine_WithNoConduit_SealsRatherThanHangs()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);
        await using var pilgrimNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);

        var signet = Signet.Generate(hostNode.Suite);

        var (pilgrimSide, hostSide, listener) = await ConnectAsync(ct);
        await using var _listener = listener;

        // A Shrine hosting the Oracle only — exactly what every 0.3.4-shaped site is.
        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(hostSide, signet, new DelegateOracleHandler(NoPage), ct);

        await using var shrine = await pilgrimNode.PilgrimageOverVesselAsync(pilgrimSide, signet.Sigil, ct);
        await shrine.Conduits.SendAsync(
            new ConduitFrame { ProtocolId = 9, SchemaVersion = 1, Flags = 0, Payload = "anyone there?"u8.ToArray() }, ct);

        var answer = await shrine.Conduits.ReceiveAsync(ct);

        Assert.NotNull(answer);
        Assert.True(answer.IsSealed);
        Assert.Equal("no conduit hosted", answer.SealReason);
    }

    [Fact]
    public async Task Conduit_Handler_ThatThrows_SealsOnlyTheConduit()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);
        await using var pilgrimNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);

        var signet = Signet.Generate(hostNode.Suite);
        var conduit = new DelegateConduitHandler((_, _) => throw new InvalidOperationException("internals no Pilgrim should see"));

        var (pilgrimSide, hostSide, listener) = await ConnectAsync(ct);
        await using var _listener = listener;

        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(
            hostSide, signet, new DelegateOracleHandler(req => OracleResponse.Text($"page:{req.Path}")), null, null, conduit, ct);

        await using var shrine = await pilgrimNode.PilgrimageOverVesselAsync(pilgrimSide, signet.Sigil, ct);

        var sealedFrame = await shrine.Conduits.ReceiveAsync(ct);
        Assert.NotNull(sealedFrame);
        Assert.True(sealedFrame.IsSealed);
        Assert.Equal("failed", sealedFrame.SealReason);          // never the exception text

        // The Oracle is untouched: one bad rite does not take the Shrine down.
        var page = await shrine.ConsultAsync(OracleRequest.Get("/still-here"), ct);
        Assert.Equal("page:/still-here", page.AsText());
    }

    [Fact]
    public void PilgrimageConduit_RefusesAPayloadPastTheBrowserCeiling()
    {
        var frame = new ConduitFrame
        {
            ProtocolId = 1,
            SchemaVersion = 1,
            Flags = 0,
            Payload = new byte[ConduitCodec.MaxPayloadBytes + 1],
        };

        var error = Assert.Throws<CodexFormatException>(() => ConduitCodec.EncodeVersioned(frame));
        Assert.Contains("Relic", error.Message);                 // says where the bytes should go instead
    }

    [Fact]
    public void ArcanumConduit_KeepsItsFrameSizedCeiling()
    {
        // The channel path has no browser on it, so the 192 KiB ceiling must not leak onto it.
        var frame = new ConduitFrame
        {
            ProtocolId = 1,
            SchemaVersion = 1,
            Flags = 0,
            Payload = new byte[ConduitCodec.MaxPayloadBytes * 2],
        };

        var encoded = ConduitCodec.Encode(frame);
        Assert.Equal(frame.Payload.Length, ConduitCodec.Decode(encoded).Payload.Length);
    }

    [Fact]
    public void PilgrimageConduit_CarriesItsCupriMarkVersion()
    {
        var frame = new ConduitFrame { ProtocolId = 4, SchemaVersion = 1, Flags = 0, Payload = [1, 2, 3] };
        var encoded = ConduitCodec.EncodeVersioned(frame);

        Assert.Equal((byte)CupriMarks.Supported(CupriMarks.Conduit).Max, encoded[0]);
        Assert.Equal(frame.Payload, ConduitCodec.DecodeVersioned(encoded).Payload);

        encoded[0] = 200;                                        // a version this build has never heard of
        Assert.Throws<CodexFormatException>(() => ConduitCodec.DecodeVersioned(encoded));
    }

    [Fact]
    public void ArcanumConduit_WireIsUnchanged_ByTheVersionByte()
    {
        // The pre-catalogue channel wire must stay byte-for-byte what a 0.3.4 peer speaks.
        var frame = new ConduitFrame { ProtocolId = 42, SchemaVersion = 3, Flags = 0x01, Payload = [9, 8, 7] };
        var channel = ConduitCodec.Encode(frame);
        var pilgrimage = ConduitCodec.EncodeVersioned(frame);

        Assert.Equal(channel.Length + 1, pilgrimage.Length);
        Assert.Equal(channel, pilgrimage[1..]);
    }

    [Fact]
    public void ConduitPadding_QuantisesUpButNeverPastTheCeiling()
    {
        Assert.Equal(512, ConduitPadding.Default.Quantise(1, ConduitCodec.MaxPaddedBytes));
        Assert.Equal(1024, ConduitPadding.Default.Quantise(513, ConduitCodec.MaxPaddedBytes));
        Assert.Equal(512, ConduitPadding.Default.Quantise(512, ConduitCodec.MaxPaddedBytes));
        Assert.Equal(77, ConduitPadding.None.Quantise(77, ConduitCodec.MaxPaddedBytes));
        Assert.Equal(1024, ConduitPadding.PowersOfTwo.Quantise(600, ConduitCodec.MaxPaddedBytes));

        // An at-limit frame is left alone rather than grown into one the transport would refuse.
        var atLimit = ConduitCodec.MaxPaddedBytes;
        Assert.Equal(atLimit, ConduitPadding.Default.Quantise(atLimit, ConduitCodec.MaxPaddedBytes));
    }

    [Fact]
    public void PaddingIsInvisible_ToTheDecoder()
    {
        // Padding rides inside what is sealed and is never parsed: every field is length-prefixed.
        var frame = new ConduitFrame { ProtocolId = 4, SchemaVersion = 1, Flags = 0, Payload = [1, 2, 3] };
        var encoded = ConduitCodec.EncodeVersioned(frame);
        var padded = new byte[ConduitPadding.Default.Quantise(encoded.Length, ConduitCodec.MaxPaddedBytes)];
        encoded.CopyTo(padded, 0);

        var decoded = ConduitCodec.DecodeVersioned(padded);
        Assert.Equal(frame.Payload, decoded.Payload);
        Assert.Equal(4u, decoded.ProtocolId);
    }

    [Fact]
    public async Task SealedFrame_IsTheReasonThenEndOfStream()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);
        await using var pilgrimNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "sites.example" }, ct);

        var signet = Signet.Generate(hostNode.Suite);

        var (pilgrimSide, hostSide, listener) = await ConnectAsync(ct);
        await using var _listener = listener;

        // A Shrine with no conduit: it seals, then holds the visit open for the Oracle.
        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(hostSide, signet, new DelegateOracleHandler(NoPage), ct);

        await using var shrine = await pilgrimNode.PilgrimageOverVesselAsync(pilgrimSide, signet.Sigil, ct);
        await shrine.Conduits.SendAsync(
            new ConduitFrame { ProtocolId = 9, SchemaVersion = 1, Flags = 0, Payload = "anyone?"u8.ToArray() }, ct);

        var first = await shrine.Conduits.ReceiveAsync(ct);
        Assert.NotNull(first);
        Assert.True(first.IsSealed);
        Assert.Equal("no conduit hosted", first.SealReason);

        // The reason once, then end of stream — never a wait for a close the host will not send, since it is still
        // serving the Oracle over the same visit.
        Assert.Null(await shrine.Conduits.ReceiveAsync(ct));
        Assert.Null(await shrine.Conduits.ReceiveAsync(ct));

        var page = await shrine.ConsultAsync(OracleRequest.Get("/still-here"), ct);
        Assert.Equal(200u, page.Status);
    }

    [Fact]
    public async Task ArcanumConduit_LeavesTheTopFlagBitToTheApplication()
    {
        // The channel wire predates the reserved bit. A pre-0.3.5 application may already be using the top flag, so
        // reading it as a seal there would end that conduit silently — the flag day CupriMark exists to avoid.
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();
        var sessionKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(suite.Aead.KeySize);

        var (a, b, listener) = await ConnectAsync(ct);
        await using var _a = a;
        await using var _b = b;
        await using var _l = listener;

        var sender = new ConduitSession(a, sessionKey, suite);
        var receiver = new ConduitSession(b, sessionKey, suite);

        await sender.SendAsync(new ConduitFrame
        {
            ProtocolId = 1,
            SchemaVersion = 1,
            Flags = ConduitFlags.Sealed | 0x2,      // an application using the top bit as its own
            Payload = "still mine"u8.ToArray(),
        }, ct);

        var got = await receiver.ReceiveAsync(ct);
        Assert.NotNull(got);
        Assert.Equal("still mine"u8.ToArray(), got.Payload);

        // The stream is NOT latched shut on the channel path: a second frame still arrives.
        await sender.SendAsync(new ConduitFrame
        { ProtocolId = 1, SchemaVersion = 1, Flags = 0, Payload = "and another"u8.ToArray() }, ct);

        var second = await receiver.ReceiveAsync(ct);
        Assert.NotNull(second);
        Assert.Equal("and another"u8.ToArray(), second.Payload);
    }

    [Fact]
    public void ApplicationFlags_MaskEveryReservedBit()
    {
        var frame = new ConduitFrame
        {
            ProtocolId = 1,
            SchemaVersion = 1,
            Flags = ConduitFlags.Sealed | 0x2 | 0x40,
            Payload = [],
        };

        Assert.Equal(0x42u, frame.ApplicationFlags);
        Assert.True(frame.IsSealed);
        Assert.Equal(0u, frame.ApplicationFlags & ConduitFlags.Reserved);
    }

    [Fact]
    public async Task PilgrimageConduit_AcceptsExactlyTheCeiling()
    {
        // The number a consumer sizes its chunking against: the check is on the raw payload, before any padding.
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        var (a, b, listener) = await ConnectAsync(ct);
        await using var _a = a;
        await using var _b = b;
        await using var _l = listener;

        var sender = new ConduitSession(new VesselStreamChannel(a, ConduitSession.DataStream));
        var receiver = new ConduitSession(new VesselStreamChannel(b, ConduitSession.DataStream));

        Assert.Equal(ConduitCodec.MaxPayloadBytes, sender.MaxPayloadBytes);

        await sender.SendAsync(new ConduitFrame
        {
            ProtocolId = 1,
            SchemaVersion = 1,
            Flags = 0,
            Payload = new byte[ConduitCodec.MaxPayloadBytes],
        }, ct);

        var got = await receiver.ReceiveAsync(ct);
        Assert.NotNull(got);
        Assert.Equal(ConduitCodec.MaxPayloadBytes, got.Payload.Length);
    }

    [Fact]
    public async Task ArcanumConduit_MaxPayload_ActuallyFitsAVesselFrame()
    {
        // The ceiling must be reachable. Set equal to the frame size it was not: a payload at exactly 16 MiB encoded
        // to 17 bytes more than a frame may carry, so the documented maximum could never be sent, and the refusal
        // came from FrameCodec at the transport rather than from the rite.
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();
        var sessionKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(suite.Aead.KeySize);

        var (a, b, listener) = await ConnectAsync(ct);
        await using var _a = a;
        await using var _b = b;
        await using var _l = listener;

        var sender = new ConduitSession(a, sessionKey, suite);
        var receiver = new ConduitSession(b, sessionKey, suite);

        Assert.True(ConduitCodec.MaxChannelPayloadBytes < FrameCodec.DefaultMaxFrameSize,
            "the channel ceiling must leave room for framing, the Veil and the vessel header");

        await sender.SendAsync(new ConduitFrame
        {
            ProtocolId = 1,
            SchemaVersion = 1,
            Flags = 0,
            Payload = new byte[ConduitCodec.MaxChannelPayloadBytes],
        }, ct);

        var got = await receiver.ReceiveAsync(ct);
        Assert.NotNull(got);
        Assert.Equal(ConduitCodec.MaxChannelPayloadBytes, got.Payload.Length);
    }

    [Fact]
    public void ArcanumConduit_RefusesPastItsCeiling_AtTheRite()
    {
        var error = Assert.Throws<CodexFormatException>(() => ConduitCodec.Encode(new ConduitFrame
        {
            ProtocolId = 1,
            SchemaVersion = 1,
            Flags = 0,
            Payload = new byte[ConduitCodec.MaxChannelPayloadBytes + 1],
        }));

        // Named by the rite, with the rite's own number — never FrameCodec talking about frame bytes.
        Assert.Contains($"{ConduitCodec.MaxChannelPayloadBytes}", error.Message);
    }

    [Fact]
    public async Task Conduit_SerialisesConcurrentSends()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        var (a, b, listener) = await ConnectAsync(ct);
        await using var _a = a;
        await using var _b = b;
        await using var _l = listener;

        var sender = new ConduitSession(new VesselStreamChannel(a, ConduitSession.DataStream));
        var receiver = new ConduitSession(new VesselStreamChannel(b, ConduitSession.DataStream));

        const int count = 32;
        await Task.WhenAll(Enumerable.Range(0, count).Select(i => sender.SendAsync(
            new ConduitFrame { ProtocolId = (uint)i, SchemaVersion = 1, Flags = 0, Payload = new byte[64] }, ct)));

        // Every frame must arrive whole and parseable — interleaved writes would corrupt the framing instead.
        var seen = new HashSet<uint>();
        for (var i = 0; i < count; i++)
        {
            var frame = await receiver.ReceiveAsync(ct);
            Assert.NotNull(frame);
            Assert.True(seen.Add(frame.ProtocolId));
        }

        Assert.Equal(count, seen.Count);
    }
}
