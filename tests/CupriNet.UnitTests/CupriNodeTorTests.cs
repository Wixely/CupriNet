using System.Collections.Concurrent;
using System.Net;
using System.Text;
using CupriNet.Abstractions;
using CupriNet.Concordance;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Persistence;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// The onion (Tor) transport seam: pairing over an <see cref="IOnionTransport"/> (proven with a fake that dials
/// loopback TCP under the hood), the cold-start gate (Tor requires a durable SecretStore), and the encrypted
/// guard-state store. The real CupriTor binding plugs into the same seam.
/// </summary>
public class CupriNodeTorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A durable (non-in-memory) store so the cold-start gate is satisfied in tests.</summary>
    private sealed class DurableStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _d = new();
        public ValueTask StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct = default) { _d[key] = secret.ToArray(); return ValueTask.CompletedTask; }
        public ValueTask<byte[]?> LoadAsync(string key, CancellationToken ct = default) => new(_d.TryGetValue(key, out var v) ? v : null);
        public ValueTask DeleteAsync(string key, CancellationToken ct = default) { _d.TryRemove(key, out _); return ValueTask.CompletedTask; }
    }

    private sealed class FakeOnionTransport(Func<string, int, Task<IVessel>> connect) : IOnionTransport
    {
        public event Action<string>? Status { add { } remove { } }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IVessel> ConnectAsync(string onion, int virtualPort, CancellationToken ct = default) => connect(onion, virtualPort);
        public Task<string> PublishAsync(int virtualPort, int localPort, CancellationToken ct = default)
            => Task.FromResult(new string('a', 56) + ".onion");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ConjoinViaOnion_PairsOverTheSeam()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        await using var b = await CupriNode.CreateAsync(
            new CupriNodeOptions { Concordium = "tor.test", EnableOverlayGossip = false }, ct);

        // Fake onion transport: "dialing an onion" actually opens a loopback TCP vessel to B's listener.
        var fake = new FakeOnionTransport(async (_, _) => await TcpVessel.ConnectAsync("127.0.0.1", b.LocalEndPoint.Port, cancellationToken: ct));
        await using var a = await CupriNode.CreateAsync(
            new CupriNodeOptions { Concordium = "tor.test", EnableOverlayGossip = false, SecretStore = new DurableStore(), OnionTransport = fake }, ct);

        var acceptB = b.AcceptAsync(ct);
        var pairA = await a.ConjoinViaOnionAsync("someonionaddress.onion", b.Identity.Sigil, now, ct);
        var pairB = await acceptB;

        Assert.Equal(b.Identity.Sigil, pairA.PeerSigil);
        Assert.Equal(a.Identity.Sigil, pairB.PeerSigil);

        await pairA.Vessel.SendAsync(3, "over-onion"u8.ToArray(), ct);
        var frame = await pairB.Vessel.ReceiveAsync(ct);
        Assert.Equal("over-onion", Encoding.UTF8.GetString(frame!.Value.Payload));

        // A's published onion surfaces as an Onion beacon (fake publishes immediately).
        while (a.OnionBeacon is null) { ct.ThrowIfCancellationRequested(); await Task.Delay(50, ct); }
        Assert.Equal(EndpointKind.Onion, a.OnionBeacon!.Kind);
    }

    [Fact]
    public async Task ConjoinAsync_TorOnlyLink_InClearnetMode_GivesAClearError()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        // An inviter that advertises ONLY an onion address — a Tor-only peer.
        await using var inviter = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "tor.test",
            EnableOverlayGossip = false,
            AdvertisedBeacons = [new Beacon(EndpointKind.Onion, new string('a', 56) + ".onion", CupriNode.OnionVirtualPort)],
        }, ct);
        var uri = inviter.IntoneUri(TimeSpan.FromHours(1), now);
        Assert.True(IntonationUri.TryParse(uri, out var intonation, out _));

        // A joiner with no Tor transport can't reach it — and should say exactly why.
        await using var joiner = await CupriNode.CreateAsync(
            new CupriNodeOptions { Concordium = "tor.test", EnableOverlayGossip = false }, ct);

        var ex = await Assert.ThrowsAsync<CupriNodeException>(async () => await joiner.ConjoinAsync(intonation!, now, ct));
        Assert.Contains("Tor", ex.Message);
        Assert.Contains("onion", ex.Message);
    }

    [Fact]
    public async Task Tor_RequiresADurableStore_ColdStartRejected()
    {
        var fake = new FakeOnionTransport((_, _) => throw new InvalidOperationException());

        // No store at all → rejected.
        await Assert.ThrowsAsync<CupriNodeException>(async () => await CupriNode.CreateAsync(
            new CupriNodeOptions { Concordium = "tor.test", OnionTransport = fake }));

        // Explicit in-memory store (a cold start) → also rejected.
        await Assert.ThrowsAsync<CupriNodeException>(async () => await CupriNode.CreateAsync(
            new CupriNodeOptions { Concordium = "tor.test", OnionTransport = fake, SecretStore = new InMemorySecretStore() }));
    }

    [Fact]
    public async Task TorOnlyMode_RequiresAnOnionTransport()
    {
        await Assert.ThrowsAsync<CupriNodeException>(async () => await CupriNode.CreateAsync(
            new CupriNodeOptions { Concordium = "tor.test", Mode = ReachabilityMode.TorOnly, SecretStore = new DurableStore() }));
    }

    [Fact]
    public async Task TorOnlyMode_AdvertisesOnlyTheOnion_AndBindsLoopback()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        var fake = new FakeOnionTransport((_, _) => throw new InvalidOperationException());
        await using var node = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "tor.test",
            EnableOverlayGossip = false,
            Mode = ReachabilityMode.TorOnly,
            SecretStore = new DurableStore(),
            OnionTransport = fake,
            ListenAddress = IPAddress.Any, // TorOnly must override this to loopback
        }, ct);

        // Listener bound to loopback despite requesting Any — never reachable directly on clearnet.
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)node.LocalEndPoint).Address);

        while (node.OnionBeacon is null) { ct.ThrowIfCancellationRequested(); await Task.Delay(50, ct); }
        var uri = node.IntoneUri(TimeSpan.FromHours(1), now);
        Assert.True(IntonationUri.TryParse(uri, out var intonation, out _));

        // The link carries ONLY the onion beacon — no Host/Mapped that would tie it to an IP.
        Assert.NotEmpty(intonation!.Beacons);
        Assert.All(intonation.Beacons, b => Assert.Equal(EndpointKind.Onion, b.Kind));
    }

    [Fact]
    public async Task TorOnlyMode_RefusesAClearnetLink()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        // A normal clearnet inviter (Host beacon).
        await using var inviter = await CupriNode.CreateAsync(
            new CupriNodeOptions { Concordium = "tor.test", EnableOverlayGossip = false }, ct);
        var uri = inviter.IntoneUri(TimeSpan.FromHours(1), now);
        Assert.True(IntonationUri.TryParse(uri, out var link, out _));

        var fake = new FakeOnionTransport((_, _) => throw new InvalidOperationException());
        await using var joiner = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "tor.test", EnableOverlayGossip = false,
            Mode = ReachabilityMode.TorOnly, SecretStore = new DurableStore(), OnionTransport = fake,
        }, ct);

        var ex = await Assert.ThrowsAsync<CupriNodeException>(async () => await joiner.ConjoinAsync(link!, now, ct));
        Assert.Contains("clearnet", ex.Message);
    }

    [Fact]
    public async Task TorOnly_Overlay_DropsClearnetRecords_KeepsOnionRecords()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        var fake = new FakeOnionTransport((_, _) => throw new InvalidOperationException());
        await using var tor = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "tor.test", EnableOverlayGossip = false,
            Mode = ReachabilityMode.TorOnly, SecretStore = new DurableStore(), OnionTransport = fake,
        }, ct);
        // A standard node, as a positive control that the filter does not break normal admission.
        await using var clear = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "tor.test", EnableOverlayGossip = false }, ct);
        // A third identity whose records we admit.
        await using var subject = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "tor.test", EnableOverlayGossip = false }, ct);

        var host = new Beacon(EndpointKind.Host, "203.0.113.5", 40000);
        var onion = new Beacon(EndpointKind.Onion, new string('b', 56) + ".onion", CupriNode.OnionVirtualPort);

        // Two distinct signer identities so admits don't collide as "updates".
        var clearnetRecord = PeerRecordSigner.Create(subject.Identity, [host], 1, PeerCapabilities.ChannelProvider, tor.Suite, now);
        var onionRecord = PeerRecordSigner.Create(clear.Identity, [onion], 1, PeerCapabilities.ChannelProvider, tor.Suite, now);

        // Tor-only node: a clearnet-only record is refused (we could only reach it over clearnet)...
        Assert.False(tor.AdmitPeer(clearnetRecord, now));
        Assert.Null(tor.Constellation.Get(subject.Identity.Sigil));

        // ...but an onion-bearing record is admitted — we can reach it over Tor.
        Assert.True(tor.AdmitPeer(onionRecord, now));
        Assert.NotNull(tor.Constellation.Get(clear.Identity.Sigil));

        // A standard node still admits the clearnet record — the partition doesn't break normal mode.
        Assert.True(clear.AdmitPeer(clearnetRecord, now));
    }

    [Fact]
    public async Task TorOnly_Overlay_DialsOnion_NeverClearnet()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        // The onion transport records what address it was asked to dial, then aborts (we only assert the lane used).
        string? dialed = null;
        var fake = new FakeOnionTransport((address, _) =>
        {
            dialed = address;
            throw new InvalidOperationException("recorded");
        });
        await using var tor = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "tor.test", EnableOverlayGossip = false,
            Mode = ReachabilityMode.TorOnly, SecretStore = new DurableStore(), OnionTransport = fake,
        }, ct);
        await using var subject = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "tor.test", EnableOverlayGossip = false }, ct);

        // A dual-stack peer: a real clearnet Host beacon AND an onion beacon. The Tor node must use ONLY the onion.
        var host = new Beacon(EndpointKind.Host, "203.0.113.5", 40000);
        var onionAddress = new string('c', 56) + ".onion";
        var onion = new Beacon(EndpointKind.Onion, onionAddress, CupriNode.OnionVirtualPort);
        var dualStack = PeerRecordSigner.Create(subject.Identity, [host, onion], 1, PeerCapabilities.ChannelProvider, tor.Suite, now);
        Assert.True(tor.AdmitPeer(dualStack, now));

        // Drive one gossip round; the only reachable peer is the dual-stack one.
        await tor.GossipOnceAsync(fanout: 1, sampleSize: 8, ct);

        // It dialled the .onion over the onion transport — never opened a clearnet socket to 203.0.113.5.
        Assert.Equal(onionAddress, dialed);
    }

    [Fact]
    public async Task SecretStoreBlobStore_RoundTrips()
    {
        var kv = new SecretStoreBlobStore(new DurableStore());
        Assert.Null(kv.Read("entry-guards"));

        kv.Write("entry-guards", new byte[] { 1, 2, 3, 4 });
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, kv.Read("entry-guards"));

        kv.Delete("entry-guards");
        Assert.Null(kv.Read("entry-guards"));
        await Task.CompletedTask;
    }
}
