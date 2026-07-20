using CupriNet.Abstractions;
using CupriNet.Codex;
using CupriNet.Core;

namespace CupriNet.Hosting;

/// <summary>
/// A persisted roll of the <see cref="KnownPeer"/>s we have Consecrated each channel with — the "Kindred"
/// we trust and can route straight back to. It is grouped by a caller-chosen channel label so an app can
/// offer, on startup, to rejoin a past channel by re-dialing exactly the peers that previously proved they
/// knew it (a successful Consecration is that proof). Backed by an <see cref="ISecretStore"/> as a single
/// encrypted-at-rest document, so it needs no enumeration support from the store.
/// </summary>
public sealed class KindredBook
{
    private const string StoreKey = "kindred/book";
    private const byte Version = 1;

    private readonly ISecretStore _store;
    private readonly object _lock = new();
    // channel label -> (sigil hex -> peer)
    private readonly Dictionary<string, Dictionary<string, KnownPeer>> _byChannel = new(StringComparer.Ordinal);

    private KindredBook(ISecretStore store) => _store = store;

    /// <summary>Loads the book from the store (an empty book if none exists).</summary>
    public static async Task<KindredBook> LoadAsync(ISecretStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var book = new KindredBook(store);
        var bytes = await store.LoadAsync(StoreKey, cancellationToken).ConfigureAwait(false);
        if (bytes is not null)
            book.Decode(bytes);
        return book;
    }

    /// <summary>The channel labels we have trusted peers for, most-recently-seen first.</summary>
    public IReadOnlyList<string> Channels()
    {
        lock (_lock)
            return _byChannel
                .OrderByDescending(kv => kv.Value.Values.Max(p => p.LastSeenUnix))
                .Select(kv => kv.Key)
                .ToList();
    }

    /// <summary>The trusted peers for a channel label, most-recently-seen first.</summary>
    public IReadOnlyList<KnownPeer> Peers(string channel)
    {
        lock (_lock)
            return _byChannel.TryGetValue(channel, out var peers)
                ? peers.Values.OrderByDescending(p => p.LastSeenUnix).ToList()
                : [];
    }

    /// <summary>Records (or refreshes) a trusted peer for a channel label and persists the book.</summary>
    public async Task RememberAsync(string channel, KnownPeer peer, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(peer);
        lock (_lock)
        {
            if (!_byChannel.TryGetValue(channel, out var peers))
                _byChannel[channel] = peers = new Dictionary<string, KnownPeer>(StringComparer.Ordinal);
            peers[Convert.ToHexStringLower(peer.Sigil.Span)] = peer;
        }
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forgets a trusted peer for a channel label and persists the book.</summary>
    public async Task ForgetAsync(string channel, Sigil sigil, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_byChannel.TryGetValue(channel, out var peers))
            {
                peers.Remove(Convert.ToHexStringLower(sigil.Span));
                if (peers.Count == 0)
                    _byChannel.Remove(channel);
            }
        }
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task SaveAsync(CancellationToken cancellationToken) => _store.StoreAsync(StoreKey, Encode(), cancellationToken).AsTask();

    private byte[] Encode()
    {
        lock (_lock)
        {
            var w = new CodexWriter();
            w.WriteByte(Version);
            w.WriteVarUInt((ulong)_byChannel.Count);
            foreach (var (channel, peers) in _byChannel)
            {
                w.WriteString(channel);
                w.WriteVarUInt((ulong)peers.Count);
                foreach (var peer in peers.Values)
                    w.WriteBytes(KnownPeerCodec.Encode(peer));
            }
            return w.ToArray();
        }
    }

    private void Decode(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        if (r.ReadByte() != Version)
            throw new CodexFormatException("Unsupported KindredBook version.");
        var channelCount = r.ReadVarUInt();
        lock (_lock)
        {
            _byChannel.Clear();
            for (var c = 0UL; c < channelCount; c++)
            {
                var channel = r.ReadString();
                var peerCount = r.ReadVarUInt();
                var peers = new Dictionary<string, KnownPeer>(StringComparer.Ordinal);
                for (var p = 0UL; p < peerCount; p++)
                {
                    var peer = KnownPeerCodec.Decode(r.ReadBytes());
                    peers[Convert.ToHexStringLower(peer.Sigil.Span)] = peer;
                }
                _byChannel[channel] = peers;
            }
        }
    }
}
