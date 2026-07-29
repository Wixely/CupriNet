using CupriNet.Hosting;
using QRCoder;

namespace CupriNet.Lodestar;

/// <summary>An immutable snapshot of the node's current connection link and its rendered QR code.</summary>
public sealed record LodestarLinkSnapshot(string Link, string QrDataUri, DateTimeOffset GeneratedAt);

/// <summary>
/// Caches this node's minted connection link (and its QR code), regenerating it only after a refresh interval
/// rather than on every request. A fresh mint rotates the nonce/timestamp and re-snapshots current reachability,
/// so the served link stays current without being expensive to fetch.
/// </summary>
public sealed class LodestarLinkProvider
{
    private readonly CupriNode _node;
    private readonly TimeSpan _lifetime;
    private readonly TimeSpan _refreshInterval;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private LodestarLinkSnapshot? _cached;

    public LodestarLinkProvider(CupriNode node, TimeSpan lifetime, TimeSpan refreshInterval, Func<DateTimeOffset>? clock = null)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _lifetime = lifetime;
        _refreshInterval = refreshInterval;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Returns the cached link, minting a new one only if the cache is empty or older than the refresh interval.</summary>
    public LodestarLinkSnapshot Current()
    {
        var now = _clock();
        lock (_gate)
        {
            if (_cached is { } c && now - c.GeneratedAt < _refreshInterval)
                return c;

            var link = _node.IntoneUri(_lifetime, now);
            _cached = new LodestarLinkSnapshot(link, RenderQr(link), now);
            return _cached;
        }
    }

    private static string RenderQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}
