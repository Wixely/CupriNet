using CupriNet.Arcanum;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Full-stack integration: two real CupriNode instances (default secure suite) complete the entire path —
/// intone → conjoin (transport pairing) → consecrate (channel session) → exchange an Epistle — with no
/// hand-assembly of the subsystems.
/// </summary>
public class CupriNodeIntegrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TwoNodes_Intone_Conjoin_Consecrate_ExchangeMessage()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);
        await using var joiner = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "example.chat" }, ct);

        // Host mints a connection URL; the joiner parses and validates it.
        var uri = host.IntoneUri(TimeSpan.FromHours(2), Now);
        Assert.True(IntonationUri.TryParse(uri, out var intonation, out _));

        // Pair at the transport layer (Conjunction), both sides concurrently.
        var acceptTask = host.AcceptAsync(ct);
        await using var pairedFromJoiner = await joiner.ConjoinAsync(intonation, Now, ct);
        await using var pairedFromHost = await acceptTask;

        Assert.Equal(host.Identity.Sigil, pairedFromJoiner.PeerSigil);
        Assert.Equal(joiner.Identity.Sigil, pairedFromHost.PeerSigil);

        // Consecrate a channel with a shared Watchword.
        var watchword = Watchword.Generate("Dungeons&Dragons");
        var joinerSession = joiner.ConsecrateAsync(pairedFromJoiner, watchword, Now, ct);
        var hostSession = host.ConsecrateAsync(pairedFromHost, watchword, Now, ct);

        await using var joinerChannel = await joinerSession;
        await using var hostChannel = await hostSession;

        Assert.Equal(joinerChannel.Epoch, hostChannel.Epoch);

        // Exchange a message end-to-end over the encrypted channel session.
        await joinerChannel.SendTextAsync("Hello, Arcanum!", Now, ct);
        var received = await hostChannel.Epistles.ReceiveAsync(ct);
        var message = Assert.IsType<MessageReceived>(received);
        Assert.Equal("Hello, Arcanum!", message.Epistle.AsText());

        // Acknowledge back.
        await hostChannel.Epistles.SendAttestationAsync(message.Epistle.MessageId, ct);
        var ack = await joinerChannel.Epistles.ReceiveAsync(ct);
        var attestation = Assert.IsType<AttestationReceived>(ack);
        Assert.Equal(message.Epistle.MessageId, attestation.MessageId);

        // The message and data rites run concurrently over the one demultiplexed session.
        var epistleTask = hostChannel.Epistles.ReceiveAsync(ct);
        var conduitTask = hostChannel.Conduits.ReceiveAsync(ct);
        await joinerChannel.Conduits.SendAsync(new ConduitFrame { ProtocolId = 9, SchemaVersion = 1, Flags = 0, Payload = "data"u8.ToArray() }, ct);
        await joinerChannel.SendTextAsync("second", Now, ct);

        var secondMessage = Assert.IsType<MessageReceived>(await epistleTask);
        var dataFrame = await conduitTask;
        Assert.Equal("second", secondMessage.Epistle.AsText());
        Assert.NotNull(dataFrame);
        Assert.Equal(9u, dataFrame.ProtocolId);
    }

    [Fact]
    public async Task Conjoin_WrongNetwork_IsRejected()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        await using var host = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "net.a" }, ct);
        await using var joiner = await CupriNode.CreateAsync(new CupriNodeOptions { Concordium = "net.b" }, ct);

        var intonation = host.Intone(TimeSpan.FromHours(2), Now);

        // Joiner is on a different network, so the host's Intonation fails validation before dialing.
        await Assert.ThrowsAsync<CupriNodeException>(async () => await joiner.ConjoinAsync(intonation, Now, ct));
    }
}
