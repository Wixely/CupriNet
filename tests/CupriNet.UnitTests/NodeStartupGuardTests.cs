using CupriNet.Arcanum;
using CupriNet.Concordance;
using CupriNet.Hosting;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Settings a node refuses to start with, and the one piece of its Tribute policy it now tells a publisher.
/// </summary>
public class NodeStartupGuardTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ANode_CannotBeStarted_OnAnInsecureSuite()
    {
        // The Simulacrum stays usable for what it is for — codec and seam tests — but not as a node's suite,
        // where it would mint links and records signed with nothing.
        var error = await Assert.ThrowsAsync<CupriNodeException>(() => CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "example.chat",
            Suite = CryptoSuites.Simulacrum(),
        }));
        Assert.Contains("no security", error.Message);
    }

    [Fact]
    public async Task ARequiredTribute_AboveTheCeiling_IsRefusedAtStartup_NotQuietlyLowered()
    {
        await Assert.ThrowsAsync<CupriNodeException>(() => CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "example.chat",
            RequiredTributeDifficulty = Tribute.MaxDifficulty + 1,
        }));
        await Assert.ThrowsAsync<CupriNodeException>(() => CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "example.chat",
            EnableFerryman = true,
            RequiredFerrymanTributeDifficulty = Tribute.MaxDifficulty + 1,
        }));
    }

    [Fact]
    public void TheTributeHint_RoundTrips_AndNothingElseParsesAsOne()
    {
        Assert.True(OverlayControl.TryReadRequiredTribute(OverlayControl.TributeTooLowResponse(18), out var required));
        Assert.Equal(18, required);

        Assert.False(OverlayControl.TryReadRequiredTribute(OverlayControl.StatusResponse(OverlayControl.StatusOk), out _));
        Assert.False(OverlayControl.TryReadRequiredTribute(OverlayControl.StatusResponse(OverlayControl.StatusRejected), out _));
        Assert.False(OverlayControl.TryReadRequiredTribute([OverlayControl.StatusTributeTooLow, (byte)(Tribute.MaxDifficulty + 1)], out _));
    }

    [Fact]
    public async Task APublisher_ThatUnderpays_IsToldThePrice_AndPaysIt()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;

        Task<CupriNode> NodeAsync(int pays, int requires) => CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = "overlay.test",
            EnableReflexiveDiscovery = false,
            EnableOverlayGossip = false,
            TributeDifficulty = pays,
            RequiredTributeDifficulty = requires,
        }, ct);

        // The publisher pays 4 bits by default; the only holder it knows wants 14.
        await using var publisher = await NodeAsync(pays: 4, requires: 4);
        await using var holder = await NodeAsync(pays: 14, requires: 14);
        await using var seeker = await NodeAsync(pays: 4, requires: 4);

        // The publisher and the seeker each know only the holder, so the holder is the one place the advert can
        // be left and the one place it can be found.
        Assert.True(publisher.AdmitPeer(holder.SelfRecord(now), now));
        Assert.True(seeker.AdmitPeer(holder.SelfRecord(now), now));
        var serving = Task.Run(async () => { try { await holder.AcceptAsync(ct); } catch { /* teardown */ } });

        var watchword = Watchword.Generate("Underpaid");
        await publisher.PublishChannelAsync(watchword, now, ct);

        var providers = await seeker.FindChannelProvidersAsync(watchword, now, ct);
        Assert.Contains(providers, d => d.ProviderSigil == publisher.Identity.Sigil);
    }
}
