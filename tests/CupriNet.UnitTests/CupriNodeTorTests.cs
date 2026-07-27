using System.Collections.Concurrent;
using System.Text;
using CupriNet.Abstractions;
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
