using CupriNet.Abstractions;
using CupriNet.Arcanum;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Persistence;
using CupriNet.Rites;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>The trusted-peer ("Kindred") reconnection path: cache a Consecrated peer and re-dial it directly later.</summary>
public class TrustedPeerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static KnownPeer Peer(byte tag, params Beacon[] beacons)
    {
        var sealKey = new byte[32];
        Array.Fill(sealKey, tag);
        return new KnownPeer
        {
            Sigil = Sigil.FromSealPublicKey(sealKey),
            SealPublicKey = sealKey,
            Beacons = beacons,
            LastSeenUnix = 1000 + tag,
        };
    }

    [Fact]
    public void KnownPeer_Codec_RoundTrips()
    {
        var peer = Peer(7, new Beacon(EndpointKind.Host, "10.0.0.5", 43820), new Beacon(EndpointKind.Manual, "example.org", 55));
        var decoded = KnownPeerCodec.Decode(KnownPeerCodec.Encode(peer));

        Assert.Equal(peer.Sigil, decoded.Sigil);
        Assert.Equal(peer.SealPublicKey, decoded.SealPublicKey);
        Assert.Equal(2, decoded.Beacons.Count);
        Assert.Equal("10.0.0.5", decoded.Beacons[0].Host);
        Assert.Equal(43820, decoded.Beacons[0].Port);
        Assert.Equal(peer.LastSeenUnix, decoded.LastSeenUnix);
    }

    [Fact]
    public async Task KindredBook_Remembers_Lists_Forgets_AndPersists()
    {
        var store = new InMemorySecretStore();
        var book = await KindredBook.LoadAsync(store);

        var alice = Peer(1, new Beacon(EndpointKind.Host, "10.0.0.1", 1));
        var bob = Peer(2, new Beacon(EndpointKind.Host, "10.0.0.2", 2));
        await book.RememberAsync("Room#secret", alice);
        await book.RememberAsync("Room#secret", bob);
        await book.RememberAsync("Other#x", alice);

        Assert.Equal(2, book.Peers("Room#secret").Count);
        Assert.Contains("Room#secret", book.Channels());
        Assert.Contains("Other#x", book.Channels());

        // Reloading from the same store reads the persisted book back.
        var reloaded = await KindredBook.LoadAsync(store);
        Assert.Equal(2, reloaded.Peers("Room#secret").Count);
        Assert.Single(reloaded.Peers("Other#x"));

        await reloaded.ForgetAsync("Room#secret", alice.Sigil);
        Assert.Single(reloaded.Peers("Room#secret"));
        Assert.Equal(bob.Sigil, reloaded.Peers("Room#secret")[0].Sigil);
    }

    [Fact]
    public async Task Reconnect_RedialsTrustedPeer_WithoutFreshIntonation()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);
        await using var joiner = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);

        // First contact via an Intonation, so the joiner learns the host's dialable beacons.
        var uri = host.IntoneUri(TimeSpan.FromHours(2), Now);
        Assert.True(IntonationUri.TryParse(uri, out var intonation, out _));
        var acceptTask = host.AcceptAsync(ct);
        await using (var firstJoin = await joiner.ConjoinAsync(intonation, Now, ct))
        await using (var firstHost = await acceptTask)
        {
            Assert.Equal(host.Identity.Sigil, firstJoin.PeerSigil);
        }

        // Cache the host as a trusted peer and reconnect later with NO new Intonation.
        var known = new KnownPeer
        {
            Sigil = host.Identity.Sigil,
            SealPublicKey = host.Identity.Seal.PublicKey,
            Beacons = intonation.Beacons,
            LastSeenUnix = Now.ToUnixTimeSeconds(),
        };

        var reAccept = host.AcceptAsync(ct);
        await using var reJoin = await joiner.ReconnectAsync(known, ct);
        await using var reHost = await reAccept;

        Assert.Equal(host.Identity.Sigil, reJoin.PeerSigil);
        Assert.Equal(joiner.Identity.Sigil, reHost.PeerSigil);

        // And the re-dialed pair can Consecrate and talk (both sides handshake concurrently).
        var watchword = Watchword.Generate("TrustedRoom");
        var joinerConsecrate = joiner.ConsecrateAsync(reJoin, watchword, Now, ct);
        var hostConsecrate = host.ConsecrateAsync(reHost, watchword, Now, ct);
        await using var joinerChannel = await joinerConsecrate;
        await using var hostChannel = await hostConsecrate;

        await joinerChannel.SendTextAsync("back again", Now, ct);
        var received = Assert.IsType<MessageReceived>(await hostChannel.Epistles.ReceiveAsync(ct));
        Assert.Equal("back again", received.Epistle.AsText());
    }

    [Fact]
    public async Task Reconnect_WrongSigilAtAddress_IsRejected()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);
        await using var joiner = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);

        var uri = host.IntoneUri(TimeSpan.FromHours(2), Now);
        Assert.True(IntonationUri.TryParse(uri, out var intonation, out _));

        // A trusted record that names the host's address but the WRONG (joiner's) identity.
        var mismatched = new KnownPeer
        {
            Sigil = joiner.Identity.Sigil,
            SealPublicKey = joiner.Identity.Seal.PublicKey,
            Beacons = intonation.Beacons,
            LastSeenUnix = Now.ToUnixTimeSeconds(),
        };

        var acceptTask = host.AcceptAsync(ct);
        await Assert.ThrowsAsync<CupriNodeException>(async () => await joiner.ReconnectAsync(mismatched, ct));
        // Drain the responder side so the listener isn't left hanging.
        try { await using var _ = await acceptTask; } catch { /* the initiator aborted after the Toll/handshake */ }
    }
}
