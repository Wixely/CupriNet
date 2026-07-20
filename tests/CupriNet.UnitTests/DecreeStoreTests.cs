using CupriNet.Alembic;
using CupriNet.Arcanum;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

public class DecreeStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly byte[] GlyphA = new byte[32]; // all zeros
    private static readonly byte[] GlyphB = Enumerable.Repeat((byte)0x11, 32).ToArray();

    private static ICryptoSuite Suite() => CryptoSuites.Simulacrum();

    private static Decree Decree(ICryptoSuite suite, byte[] glyph, DateTimeOffset expires, NodeIdentity? provider = null, ulong seq = 1)
        => DecreeSigner.Create(provider ?? NodeIdentity.Generate(suite), glyph, [], expires, seq, suite);

    [Fact]
    public void Publish_ThenLookup_ReturnsIt()
    {
        var suite = Suite();
        var store = new DecreeStore();
        var decree = Decree(suite, GlyphA, Now.AddMinutes(20));

        Assert.Equal(DecreePublishResult.Published, store.Publish(decree, Now));
        var found = store.Lookup(GlyphA, Now);
        Assert.Single(found);
        Assert.Equal(decree.ProviderSigil, found[0].ProviderSigil);
    }

    [Fact]
    public void ExpiredDecrees_AreRejected_AndPrunedFromLookup()
    {
        var suite = Suite();
        var store = new DecreeStore();

        Assert.Equal(DecreePublishResult.Rejected, store.Publish(Decree(suite, GlyphA, Now.AddMinutes(-1)), Now));

        var live = Decree(suite, GlyphA, Now.AddMinutes(5));
        store.Publish(live, Now);
        Assert.Empty(store.Lookup(GlyphA, Now.AddMinutes(10))); // expired by lookup time
    }

    [Fact]
    public void SameProvider_NewerSequence_Updates_OlderRejected()
    {
        var suite = Suite();
        var store = new DecreeStore();
        var provider = NodeIdentity.Generate(suite);

        store.Publish(Decree(suite, GlyphA, Now.AddMinutes(20), provider, seq: 1), Now);
        Assert.Equal(DecreePublishResult.Updated, store.Publish(Decree(suite, GlyphA, Now.AddMinutes(20), provider, seq: 2), Now));
        Assert.Equal(DecreePublishResult.Rejected, store.Publish(Decree(suite, GlyphA, Now.AddMinutes(20), provider, seq: 2), Now));

        Assert.Single(store.Lookup(GlyphA, Now)); // still one entry for the provider
    }

    [Fact]
    public void PerGlyphQuota_EvictsSoonestToExpire_OrRejectsShorterLived()
    {
        var suite = Suite();
        var store = new DecreeStore(new DecreeStoreOptions { MaxDecreesPerGlyph = 2 });

        store.Publish(Decree(suite, GlyphA, Now.AddMinutes(10)), Now);
        store.Publish(Decree(suite, GlyphA, Now.AddMinutes(10)), Now);

        // A longer-lived newcomer evicts a soonest-to-expire entry.
        var longLived = Decree(suite, GlyphA, Now.AddMinutes(20));
        Assert.Equal(DecreePublishResult.Published, store.Publish(longLived, Now));
        var after = store.Lookup(GlyphA, Now);
        Assert.Equal(2, after.Count);
        Assert.Contains(after, d => d.ProviderSigil == longLived.ProviderSigil);

        // A shorter-lived newcomer is refused when the bucket is full.
        Assert.Equal(DecreePublishResult.Rejected, store.Publish(Decree(suite, GlyphA, Now.AddMinutes(1)), Now));
        Assert.Equal(2, store.Lookup(GlyphA, Now).Count);
    }

    [Fact]
    public void LookupWindow_UnionsGlyphs_AndDedupsByProvider()
    {
        var suite = Suite();
        var store = new DecreeStore();
        var provider = NodeIdentity.Generate(suite);

        // Same provider advertised under two epoch Glyphs; the window should return it once.
        store.Publish(Decree(suite, GlyphA, Now.AddMinutes(20), provider, seq: 1), Now);
        store.Publish(Decree(suite, GlyphB, Now.AddMinutes(20), provider, seq: 2), Now);
        store.Publish(Decree(suite, GlyphB, Now.AddMinutes(20)), Now); // a different provider

        var window = store.LookupWindow([GlyphA, GlyphB], Now);
        Assert.Equal(2, window.Count); // two distinct providers, not three entries
    }
}
