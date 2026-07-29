using CupriNet.Hosting;
using CupriNet.Lodestar;
using Xunit;

namespace CupriNet.UnitTests;

public class LodestarLinkProviderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Current_CachesTheLink_AndRegeneratesOnlyAfterTheInterval()
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var node = await CupriNode.CreateAsync(
            new CupriNodeOptions { Concordium = "example.chat", EnableOverlayGossip = false }, cts.Token);

        var now = DateTimeOffset.UnixEpoch.AddYears(56);
        var provider = new LodestarLinkProvider(
            node, lifetime: TimeSpan.FromHours(2), refreshInterval: TimeSpan.FromSeconds(30), clock: () => now);

        var first = provider.Current();
        Assert.StartsWith("cuprinet://intone/", first.Link);
        Assert.StartsWith("data:image/png;base64,", first.QrDataUri);

        // Within the interval: the same cached snapshot is served — the link is NOT minted per request.
        now = now.AddSeconds(29);
        var cached = provider.Current();
        Assert.Same(first, cached);

        // Past the interval: a fresh link is minted (new nonce/timestamp), so it differs from the first.
        now = now.AddSeconds(2);
        var refreshed = provider.Current();
        Assert.NotSame(first, refreshed);
        Assert.NotEqual(first.Link, refreshed.Link);
    }
}
