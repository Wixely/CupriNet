using CupriNet.Abstractions;
using CupriNet.Hosting;
using Xunit;

namespace CupriNet.UnitTests;

public class KnownRelaysTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static Sigil Sig(byte seed)
    {
        var bytes = new byte[Sigil.Size];
        Array.Fill(bytes, seed);
        return new Sigil(bytes);
    }

    [Fact]
    public void FirstUseIsNew_ThenKnownAfterApproval()
    {
        var relays = new KnownRelays();
        var b = Sig(1);

        Assert.Equal(RelayTrust.New, relays.Evaluate(b));
        Assert.False(relays.IsApproved(b));

        relays.Approve(b, "HomeRelay", Now);

        Assert.Equal(RelayTrust.Known, relays.Evaluate(b, "HomeRelay"));
        Assert.True(relays.IsApproved(b));
    }

    [Fact]
    public void NameReusedWithDifferentKey_IsAConflict()
    {
        var relays = new KnownRelays();
        relays.Approve(Sig(1), "HomeRelay", Now);

        // A different key now claims the same name -> possible impersonation (SSH "identification has changed").
        Assert.Equal(RelayTrust.NameConflict, relays.Evaluate(Sig(2), "HomeRelay"));

        // The conflict verdict wins even though Sig(2) itself is otherwise "new".
        Assert.NotEqual(RelayTrust.New, relays.Evaluate(Sig(2), "HomeRelay"));
    }

    [Fact]
    public void Remove_ForgetsTheRelay()
    {
        var relays = new KnownRelays();
        relays.Approve(Sig(1), null, Now);
        Assert.True(relays.Remove(Sig(1)));
        Assert.Equal(RelayTrust.New, relays.Evaluate(Sig(1)));
        Assert.False(relays.Remove(Sig(1)));
    }

    [Fact]
    public void EncodeDecode_RoundTrips()
    {
        var relays = new KnownRelays();
        relays.Approve(Sig(1), "HomeRelay", Now);
        relays.Approve(Sig(2), null, Now.AddMinutes(5));

        var restored = KnownRelays.Decode(relays.Encode());

        Assert.True(restored.IsApproved(Sig(1)));
        Assert.True(restored.IsApproved(Sig(2)));
        Assert.Equal(RelayTrust.Known, restored.Evaluate(Sig(1), "HomeRelay"));
        Assert.Equal(RelayTrust.NameConflict, restored.Evaluate(Sig(9), "HomeRelay"));
        Assert.Equal(2, restored.All().Count);
    }

    [Fact]
    public void Decode_OfGarbage_YieldsEmptyStore()
    {
        Assert.Empty(KnownRelays.Decode([0xFF, 0x00, 0x12]).All());
        Assert.Empty(KnownRelays.Decode([]).All());
    }
}
