using System.Net;
using CupriNet.Abstractions;
using CupriNet.Codex;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

/// <summary>
/// The Relic rite: bulk content over the Shrine path, as chunked Reliquary transfers — the only way past the
/// browser transport's 256 KiB message ceiling. Every chunk is verified against the manifest as it arrives, so a
/// hostile or corrupt Shrine can fail a fetch but never corrupt one.
/// </summary>
public class RelicTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly Concordium Network = new("sites.example");

    private static byte[] Bytes(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++)
            b[i] = (byte)(i * 31 + 7);
        return b;
    }

    // ---- codec ----

    [Fact]
    public void Codec_RoundTrips_EveryKind()
    {
        foreach (var kind in Enum.GetValues<RelicFrameKind>())
        {
            var frame = new RelicFrame { Kind = kind, Name = "app/client.wasm", FileIndex = 3, ChunkIndex = 41, Payload = Bytes(96) };
            var decoded = RelicCodec.Decode(RelicCodec.Encode(frame));
            Assert.Equal(frame, decoded with { Payload = frame.Payload }); // records: compare fields, payload by content below
            Assert.Equal(frame.Payload, decoded.Payload);
        }
    }

    [Fact]
    public void Codec_Enforces_TheFrameCeiling_BothWays()
    {
        // Encoding an over-ceiling payload is refused before any I/O …
        var oversized = new RelicFrame { Kind = RelicFrameKind.Chunk, Name = "big", Payload = new byte[RelicCodec.MaxPayloadBytes + 1] };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => RelicCodec.Encode(oversized));
        Assert.Contains("ceiling", ex.Message);

        // … and a hostile frame declaring one is refused at decode. Build the wire bytes by hand.
        var w = new CodexWriter();
        w.WriteByte(1);                                    // relic v1
        w.WriteByte((byte)RelicFrameKind.Chunk);
        w.WriteString("big");
        w.WriteVarUInt(0);
        w.WriteVarUInt(0);
        w.WriteBytes(new byte[RelicCodec.MaxPayloadBytes + 1]);
        Assert.Throws<CodexFormatException>(() => RelicCodec.Decode(w.ToArray()));
    }

    [Fact]
    public void Codec_Refuses_UnknownVersion_UnknownKind_AndBadIndices()
    {
        static byte[] Wire(byte version, byte kind, ulong fileIndex = 0, ulong chunkIndex = 0)
        {
            var w = new CodexWriter();
            w.WriteByte(version);
            w.WriteByte(kind);
            w.WriteString("a");
            w.WriteVarUInt(fileIndex);
            w.WriteVarUInt(chunkIndex);
            w.WriteBytes([]);
            return w.ToArray();
        }

        // A version this catalogue does not speak (too new) is a typed refusal, not a parse of the wrong shape.
        Assert.Throws<CodexFormatException>(() => RelicCodec.Decode(Wire(version: 2, kind: 1)));
        Assert.Throws<CodexFormatException>(() => RelicCodec.Decode(Wire(version: 1, kind: 99)));
        // Indices past the Reliquary's own Wards cannot smuggle absurd values into anything that indexes with them.
        Assert.Throws<CodexFormatException>(() => RelicCodec.Decode(Wire(version: 1, kind: 2, fileIndex: 1UL << 20)));
        Assert.Throws<CodexFormatException>(() => RelicCodec.Decode(Wire(version: 1, kind: 2, chunkIndex: 1UL << 40)));
    }

    // ---- the static source ----

    [Fact]
    public void StaticSource_Refuses_UnsafeNames_And_UntravellableManifests()
    {
        var suite = CryptoSuites.Secure();
        var source = new StaticRelicSource(suite);

        Assert.Throws<RelicException>(() => source.Add("../escape.wasm", Bytes(16)));

        // A tiny chunk size on a large-enough relic makes a manifest no frame can carry (32768 chunk hashes is
        // over a megabyte of manifest): the author learns at composition time, not a Pilgrim at fetch time.
        Assert.Throws<RelicException>(() => source.Add("dense.bin", Bytes(1024 * 1024), chunkSize: 32));
    }

    // ---- end to end over a real Pilgrimage ----

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
    public async Task Relic_IsFetched_ChunkByChunk_AndVerified_WithoutAPilgrimNode()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = Network.Value }, ct);
        var signet = Signet.Generate(hostNode.Suite);
        var handler = new DelegateOracleHandler(_ => OracleResponse.Text("<h1>page</h1>", "text/html; charset=utf-8"));

        // A "blob" big enough to need many chunks, chunked small so the test proves the loop, not the network.
        var blob = Bytes(300 * 1024);
        var relics = new StaticRelicSource(hostNode.Suite).Add("app/client.wasm", blob, chunkSize: 32 * 1024);

        var (pilgrimSide, hostSide, listener) = await ConnectAsync(ct);
        await using var _listener = listener;

        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(hostSide, signet, handler, feeds: null, relics, ct);
        await using (var shrine = await Pilgrimage.OverVesselAsync(pilgrimSide, signet.Sigil, Network, hostNode.Suite, enableToll: true, ct))
        {
            var fetched = await shrine.FetchRelicAsync("app/client.wasm", cancellationToken: ct);
            Assert.Equal(blob, fetched);

            // The Oracle still answers on the same visit — the rites coexist on one connection.
            var page = await shrine.ConsultAsync(OracleRequest.Get("/index.html"), ct);
            Assert.Equal(200u, page.Status);
        }

        // The visit is over (the session above is disposed), so the host loop ends on its own.
        try { await hostTask; } catch { /* teardown */ }
    }

    [Fact]
    public async Task UnknownRelic_IsATypedRefusal()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = Network.Value }, ct);
        var signet = Signet.Generate(hostNode.Suite);
        var handler = new DelegateOracleHandler(_ => OracleResponse.NotFound());

        var (pilgrimSide, hostSide, listener) = await ConnectAsync(ct);
        await using var _listener = listener;

        // No relics at all: the Relic loop still serves, answering every request with a refusal — never a hang.
        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(hostSide, signet, handler, ct);
        await using (var shrine = await Pilgrimage.OverVesselAsync(pilgrimSide, signet.Sigil, Network, hostNode.Suite, enableToll: true, ct))
        {
            var ex = await Assert.ThrowsAsync<RelicException>(() => shrine.FetchRelicAsync("nowhere.bin", cancellationToken: ct));
            Assert.Contains("no such relic", ex.Message);
        }

        // The visit is over (the session above is disposed), so the host loop ends on its own.
        try { await hostTask; } catch { /* teardown */ }
    }

    [Fact]
    public async Task Bundle_IsRefusedByTheConvenience_AndFetchedByThePrimitives()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = Network.Value }, ct);
        var signet = Signet.Generate(hostNode.Suite);
        var handler = new DelegateOracleHandler(_ => OracleResponse.NotFound());

        var wasm = Bytes(90 * 1024);
        var css = Bytes(11 * 1024);
        var relics = new StaticRelicSource(hostNode.Suite)
            .Add("app", [("client.wasm", wasm), ("site.css", css)], chunkSize: 16 * 1024);

        var (pilgrimSide, hostSide, listener) = await ConnectAsync(ct);
        await using var _listener = listener;

        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(hostSide, signet, handler, feeds: null, relics, ct);
        await using (var shrine = await Pilgrimage.OverVesselAsync(pilgrimSide, signet.Sigil, Network, hostNode.Suite, enableToll: true, ct))
        {
            await Assert.ThrowsAsync<RelicException>(() => shrine.FetchRelicAsync("app", cancellationToken: ct));

            var manifest = await shrine.Relics.FetchManifestAsync("app", ct);
            Assert.Equal(2, manifest.Files.Count);
            Assert.Equal(wasm, await shrine.Relics.FetchFileAsync("app", manifest, 0, cancellationToken: ct));
            Assert.Equal(css, await shrine.Relics.FetchFileAsync("app", manifest, 1, cancellationToken: ct));
        }

        // The visit is over (the session above is disposed), so the host loop ends on its own.
        try { await hostTask; } catch { /* teardown */ }
    }

    [Fact]
    public async Task CorruptChunk_FailsTheFetch_NeverCorruptsIt()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = Network.Value }, ct);
        var signet = Signet.Generate(hostNode.Suite);
        var handler = new DelegateOracleHandler(_ => OracleResponse.NotFound());

        // A source that promises one content in the manifest and delivers another in the chunks: the Pilgrim's
        // per-chunk verification is the defence, not the source's honesty.
        var honest = new StaticRelicSource(hostNode.Suite).Add("app.bin", Bytes(64 * 1024), chunkSize: 16 * 1024);
        var lying = new LyingRelicSource(honest);

        var (pilgrimSide, hostSide, listener) = await ConnectAsync(ct);
        await using var _listener = listener;

        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(hostSide, signet, handler, feeds: null, lying, ct);
        await using (var shrine = await Pilgrimage.OverVesselAsync(pilgrimSide, signet.Sigil, Network, hostNode.Suite, enableToll: true, ct))
        {
            var ex = await Assert.ThrowsAsync<RelicException>(() => shrine.FetchRelicAsync("app.bin", cancellationToken: ct));
            Assert.Contains("failed verification", ex.Message);
        }

        // The visit is over (the session above is disposed), so the host loop ends on its own.
        try { await hostTask; } catch { /* teardown */ }
    }

    [Fact]
    public async Task ManifestDeclaringUncarriableChunks_IsRefusedByThePilgrim()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var hostNode = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = Network.Value }, ct);
        var signet = Signet.Generate(hostNode.Suite);
        var handler = new DelegateOracleHandler(_ => OracleResponse.NotFound());

        // A manifest whose chunks exceed the frame ceiling describes chunks that can never arrive; the Pilgrim
        // refuses it on receipt, with a reason, instead of stalling into codec failures chunk by chunk.
        var oversizedChunks = ReliquaryBuilder.Build([("huge.bin", Bytes(600 * 1024))], chunkSize: 512 * 1024, hostNode.Suite);
        var source = new FixedManifestSource(oversizedChunks);

        var (pilgrimSide, hostSide, listener) = await ConnectAsync(ct);
        await using var _listener = listener;

        var hostTask = hostNode.AcceptPilgrimageOverVesselAsync(hostSide, signet, handler, feeds: null, source, ct);
        await using (var shrine = await Pilgrimage.OverVesselAsync(pilgrimSide, signet.Sigil, Network, hostNode.Suite, enableToll: true, ct))
        {
            var ex = await Assert.ThrowsAsync<RelicException>(() => shrine.Relics.FetchManifestAsync("huge.bin", ct));
            Assert.Contains("frame ceiling", ex.Message);
        }

        // The visit is over (the session above is disposed), so the host loop ends on its own.
        try { await hostTask; } catch { /* teardown */ }
    }

    /// <summary>Serves an honest manifest but flips a byte in every chunk it delivers.</summary>
    private sealed class LyingRelicSource(IRelicSource inner) : IRelicSource
    {
        public ValueTask<ReliquaryManifest?> ResolveAsync(string name, CancellationToken cancellationToken = default)
            => inner.ResolveAsync(name, cancellationToken);

        public async ValueTask<byte[]?> ReadChunkAsync(string name, int fileIndex, int chunkIndex, CancellationToken cancellationToken = default)
        {
            var chunk = await inner.ReadChunkAsync(name, fileIndex, chunkIndex, cancellationToken);
            if (chunk is { Length: > 0 })
                chunk[0] ^= 0xFF;
            return chunk;
        }
    }

    /// <summary>Serves a fixed manifest for any name, and no chunks — enough to test manifest-time refusals.</summary>
    private sealed class FixedManifestSource(ReliquaryManifest manifest) : IRelicSource
    {
        public ValueTask<ReliquaryManifest?> ResolveAsync(string name, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ReliquaryManifest?>(manifest);

        public ValueTask<byte[]?> ReadChunkAsync(string name, int fileIndex, int chunkIndex, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<byte[]?>(null);
    }
}
