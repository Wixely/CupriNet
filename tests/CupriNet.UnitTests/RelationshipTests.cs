using CupriNet.Alembic;
using CupriNet.Alembic.Simulacrum;
using CupriNet.Core;
using CupriNet.Persistence;
using Xunit;

namespace CupriNet.UnitTests;

public class RelationshipTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static ICryptoSuite Suite() => new SimulacrumSuite(InsecureConsent.IUnderstandThisProvidesNoSecurity());

    [Fact]
    public void Establish_ProducesVerifiableGuiseBinding()
    {
        var suite = Suite();
        var me = NodeIdentity.Generate(suite);
        var peer = NodeIdentity.Generate(suite);

        var record = Relationship.Establish(me, peer.Sigil, peer.PublicKey.ToArray(), suite, Now);

        Assert.Equal(peer.Sigil, record.PeerSigil);
        Assert.True(GuiseBinding.Verify(
            suite, me.PublicKey.Span, peer.Sigil, record.GuisePublicKey, record.GuiseBindingSignature));
    }

    [Fact]
    public void RelationshipRecord_RoundTripsThroughCodec()
    {
        var suite = Suite();
        var me = NodeIdentity.Generate(suite);
        var peer = NodeIdentity.Generate(suite);
        var record = Relationship.Establish(me, peer.Sigil, peer.PublicKey.ToArray(), suite, Now);

        var decoded = RelationshipCodec.Decode(RelationshipCodec.Encode(record));

        Assert.Equal(record.PeerSigil, decoded.PeerSigil);
        Assert.Equal(record.PeerSealPublicKey, decoded.PeerSealPublicKey);
        Assert.Equal(record.GuisePrivateKey, decoded.GuisePrivateKey);
        Assert.Equal(record.GuisePublicKey, decoded.GuisePublicKey);
        Assert.Equal(record.GuiseBindingSignature, decoded.GuiseBindingSignature);
        Assert.Equal(record.EstablishedUnix, decoded.EstablishedUnix);
    }

    [Fact]
    public async Task RelationshipStore_SaveLoadDelete()
    {
        var suite = Suite();
        var me = NodeIdentity.Generate(suite);
        var peer = NodeIdentity.Generate(suite);
        var record = Relationship.Establish(me, peer.Sigil, peer.PublicKey.ToArray(), suite, Now);

        var store = new RelationshipStore(new InMemorySecretStore());
        Assert.Null(await store.LoadAsync(peer.Sigil));

        await store.SaveAsync(record);
        var loaded = await store.LoadAsync(peer.Sigil);
        Assert.NotNull(loaded);
        Assert.Equal(record.GuisePublicKey, loaded.GuisePublicKey);

        await store.DeleteAsync(peer.Sigil);
        Assert.Null(await store.LoadAsync(peer.Sigil));
    }

    [Fact]
    public async Task IdentityStore_LoadOrCreate_PersistsAndReloadsSameSigil()
    {
        var suite = Suite();
        var secretStore = new InMemorySecretStore();
        var store = new IdentityStore(secretStore);

        var created = await store.LoadOrCreateAsync(suite);
        var reloaded = await store.LoadOrCreateAsync(suite); // second call must reuse persisted identity

        Assert.Equal(created.Sigil, reloaded.Sigil);

        var directLoad = await store.LoadAsync();
        Assert.NotNull(directLoad);
        Assert.Equal(created.Sigil, directLoad.Sigil);
    }
}
