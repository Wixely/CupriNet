using CupriNet.Abstractions;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

public class SignetTests
{
    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _map = new();

        public ValueTask StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
        {
            _map[key] = secret.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_map.TryGetValue(key, out var value) ? value.ToArray() : null);

        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _map.Remove(key);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void Generate_ProducesCupriAddress_MatchingFingerprint()
    {
        var suite = CryptoSuites.Secure();
        var signet = Signet.Generate(suite);

        Assert.StartsWith("cupri1", signet.Address);
        Assert.Equal(Bech32.Fingerprint(signet.Sigil), signet.Address);
        Assert.Equal(Sigil.Size, signet.Sigil.Span.Length);
        Assert.False(signet.Sigil.IsEmpty);
    }

    [Fact]
    public void Generate_IsDistinctFromNodeIdentity()
    {
        var suite = CryptoSuites.Secure();
        var node = NodeIdentity.Generate(suite);
        var signet = Signet.Generate(suite);

        Assert.NotEqual(node.Sigil, signet.Sigil);
        Assert.NotEqual(Bech32.Fingerprint(node.Sigil), signet.Address);
    }

    [Fact]
    public async Task Store_LoadOrCreate_PersistsAndReloadsSameSignet()
    {
        var suite = CryptoSuites.Secure();
        var store = new SignetStore(new InMemorySecretStore());

        var created = await store.LoadOrCreateAsync(suite);
        var reloaded = await store.LoadOrCreateAsync(suite);

        Assert.Equal(created.Address, reloaded.Address);
        Assert.Equal(created.Seal.PrivateKey, reloaded.Seal.PrivateKey);
        Assert.Equal(created.Seal.PublicKey, reloaded.Seal.PublicKey);
    }

    [Fact]
    public async Task Store_DifferentNames_YieldDistinctStableSignets()
    {
        var suite = CryptoSuites.Secure();
        var store = new SignetStore(new InMemorySecretStore());

        var blog = await store.LoadOrCreateAsync(suite, "blog");
        var shop = await store.LoadOrCreateAsync(suite, "shop");

        Assert.NotEqual(blog.Address, shop.Address);
        Assert.Equal(blog.Address, (await store.LoadAsync("blog"))!.Address);
        Assert.Equal(shop.Address, (await store.LoadAsync("shop"))!.Address);
    }

    [Fact]
    public async Task Store_Load_ReturnsNull_WhenAbsent()
    {
        var store = new SignetStore(new InMemorySecretStore());
        Assert.Null(await store.LoadAsync("nope"));
    }
}
