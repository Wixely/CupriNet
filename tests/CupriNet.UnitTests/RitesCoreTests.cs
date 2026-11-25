using System.Security.Cryptography;
using CupriNet.Alembic;
using CupriNet.Rites;
using Xunit;

namespace CupriNet.UnitTests;

public class RitesCoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    [Theory]
    [MemberData(nameof(CryptoSuites.All), MemberType = typeof(CryptoSuites))]
    public void VeilCipher_RoundTrips(ICryptoSuite suite)
    {
        var key = new byte[suite.Aead.KeySize];
        var initiator = new VeilCipher(key, suite, "epistle", VeilRole.Initiator);
        var responder = new VeilCipher(key, suite, "epistle", VeilRole.Responder);
        byte[] plaintext = [1, 2, 3, 4, 5];

        Assert.Equal(plaintext, responder.Open(initiator.Seal(plaintext)));
        Assert.Equal(plaintext, initiator.Open(responder.Seal(plaintext)));
    }

    [Fact]
    public void VeilCipher_AFrame_OpensOnlyForTheOtherSide_OfTheSameRite_WithTheSameAssociatedData()
    {
        var suite = CryptoSuites.Secure();
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(suite.Aead.KeySize);
        var initiator = new VeilCipher(key, suite, "epistle", VeilRole.Initiator);
        var responder = new VeilCipher(key, suite, "epistle", VeilRole.Responder);

        var frame = initiator.Seal([7, 7, 7], [1]);
        Assert.Equal(new byte[] { 7, 7, 7 }, responder.Open(frame, [1]));

        // Handed back to the side that sent it: each direction has its own key, so it does not open.
        Assert.Null(initiator.Open(frame, [1]));
        // Moved to another rite on the same channel: each rite has its own keys.
        Assert.Null(new VeilCipher(key, suite, "conduit", VeilRole.Responder).Open(frame, [1]));
        // With the byte that travels outside the seal changed, or dropped.
        Assert.Null(responder.Open(frame, [2]));
        Assert.Null(responder.Open(frame));
    }

    [Fact]
    public void VeilCipher_DetectsTamper_AndWrongKey_UnderSecureSuite()
    {
        var suite = CryptoSuites.Secure();
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(suite.Aead.KeySize);
        var veil = new VeilCipher(key, suite, "epistle", VeilRole.Initiator);
        var peer = new VeilCipher(key, suite, "epistle", VeilRole.Responder);

        var sealed_ = veil.Seal([9, 9, 9]);
        var tampered = (byte[])sealed_.Clone();
        tampered[^1] ^= 0xFF;
        Assert.Null(peer.Open(tampered));

        var otherKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(suite.Aead.KeySize);
        Assert.Null(new VeilCipher(otherKey, suite, "epistle", VeilRole.Responder).Open(sealed_));
    }

    [Fact]
    public void VeilCipher_OpensAFrameOnce_ToleratesALittleReordering_AndRefusesWhatIsTooOld()
    {
        var suite = CryptoSuites.Secure();
        var key = RandomNumberGenerator.GetBytes(suite.Aead.KeySize);
        var sender = new VeilCipher(key, suite, "epistle", VeilRole.Initiator);
        var receiver = new VeilCipher(key, suite, "epistle", VeilRole.Responder);

        var frames = Enumerable.Range(0, VeilCipher.ReplayWindow + 10).Select(i => sender.Seal([(byte)i])).ToArray();
        Assert.Equal(1 + sender.Overhead, frames[0].Length);
        Assert.Equal(36, sender.Overhead); // nonce 12, sequence 8, tag 16

        // In order, then again: once each.
        Assert.Equal(new byte[] { 0 }, receiver.Open(frames[0]));
        Assert.Null(receiver.Open(frames[0]));

        // Out of order within the window is fine, and still once each.
        Assert.Equal(new byte[] { 3 }, receiver.Open(frames[3]));
        Assert.Equal(new byte[] { 1 }, receiver.Open(frames[1]));
        Assert.Null(receiver.Open(frames[1]));
        Assert.Null(receiver.Open(frames[3]));
        Assert.Equal(new byte[] { 2 }, receiver.Open(frames[2]));

        // A forgery does not move the window: frame 4 still opens after one.
        var forged = (byte[])frames[^1].Clone();
        forged[^1] ^= 1;
        Assert.Null(receiver.Open(forged));
        Assert.Equal(new byte[] { 4 }, receiver.Open(frames[4]));

        // Jump far ahead: the newest opens, and a frame never seen but now older than the window does not.
        Assert.NotNull(receiver.Open(frames[^1]));
        Assert.Null(receiver.Open(frames[5]));
        Assert.NotNull(receiver.Open(frames[^2]));
        Assert.Null(receiver.Open(frames[^2]));
    }

    [Fact]
    public void Epistle_Codec_RoundTrips_WithAndWithoutReply()
    {
        var a = Epistle.Text("hello world", Now);
        var decodedA = EpistleCodec.Decode(EpistleCodec.Encode(a));
        Assert.Equal(a.MessageId, decodedA.MessageId);
        Assert.Equal("hello world", decodedA.AsText());
        Assert.Null(decodedA.InReplyTo);

        var b = Epistle.Text("re: hello", Now, inReplyTo: a.MessageId);
        var decodedB = EpistleCodec.Decode(EpistleCodec.Encode(b));
        Assert.Equal(a.MessageId, decodedB.InReplyTo);
    }

    [Fact]
    public void Vigil_Sends_ClearsOnAck_AndBacksOff()
    {
        var vigil = new Vigil(new VigilOptions { BaseDelay = TimeSpan.FromSeconds(1) });
        var epistle = Epistle.Text("hi", Now);
        Assert.True(vigil.Enqueue(epistle, Now));
        Assert.False(vigil.Enqueue(epistle, Now)); // duplicate MessageId

        // Due immediately for the first send.
        var first = vigil.CollectDue(Now);
        Assert.Single(first.ToSend);
        Assert.Empty(first.Abandoned);

        // Not due again until the backoff elapses.
        Assert.Empty(vigil.CollectDue(Now).ToSend);
        Assert.Single(vigil.CollectDue(Now.AddSeconds(2)).ToSend);

        // Ack clears it.
        Assert.True(vigil.Acknowledge(epistle.MessageId));
        Assert.Equal(0, vigil.PendingCount);
    }

    [Fact]
    public void Vigil_Abandons_AfterMaxAttempts()
    {
        var vigil = new Vigil(new VigilOptions { MaxAttempts = 3, BaseDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(1) });
        var epistle = Epistle.Text("persist", Now);
        vigil.Enqueue(epistle, Now);

        // Attempts 1..3 send; the 4th sweep abandons it.
        for (var attempt = 1; attempt <= 3; attempt++)
            Assert.Single(vigil.CollectDue(Now.AddSeconds(attempt * 2)).ToSend);

        var final = vigil.CollectDue(Now.AddSeconds(100));
        Assert.Empty(final.ToSend);
        Assert.Single(final.Abandoned);
        Assert.Equal(0, vigil.PendingCount);
    }

    [Fact]
    public void Deduper_DetectsDuplicates_WithinCapacity()
    {
        var deduper = new EpistleDeduper(capacity: 2);
        var a = Epistle.Text("a", Now).MessageId;
        var b = Epistle.Text("b", Now).MessageId;

        Assert.True(deduper.TryMarkSeen(a));
        Assert.False(deduper.TryMarkSeen(a)); // duplicate
        Assert.True(deduper.TryMarkSeen(b));

        // 'a' and 'b' fill capacity; a third eviction test:
        var c = Epistle.Text("c", Now).MessageId;
        Assert.True(deduper.TryMarkSeen(c)); // evicts oldest (a)
        Assert.True(deduper.TryMarkSeen(a)); // 'a' was evicted, so it looks new again
    }
}
