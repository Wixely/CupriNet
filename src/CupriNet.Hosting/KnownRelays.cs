using CupriNet.Abstractions;
using CupriNet.Codex;

namespace CupriNet.Hosting;

/// <summary>The trust verdict for a relay a client is about to use, in SSH <c>known_hosts</c> terms.</summary>
public enum RelayTrust
{
    /// <summary>This relay key has never been approved before — prompt the user (SSH "authenticity can't be established").</summary>
    New,

    /// <summary>This relay key is already approved — proceed silently.</summary>
    Known,

    /// <summary>A relay is presenting a <em>name</em> previously pinned to a DIFFERENT key — possible impersonation.</summary>
    NameConflict,
}

/// <summary>One approved relay: its identity (Sigil), an optional human name, and when it was first/last approved.</summary>
public sealed record KnownRelay(Sigil Sigil, string? Name, long FirstSeenUnix, long LastSeenUnix);

/// <summary>
/// A client‑side trust store for Ferryman relays — trust‑on‑first‑use, like SSH <c>known_hosts</c>. A relay can
/// never read a session or impersonate the target (the direct D↔E handshake is Sigil‑pinned and encrypted), so
/// this store is about <em>which relays you'll expose connection metadata to</em>: approve once, remember to disk,
/// and get warned if a name later presents a different key. Pure state + (de)serialization; the app decides when
/// to prompt and persists <see cref="Encode"/> bytes through its own secret store.
/// </summary>
public sealed class KnownRelays
{
    private const byte Version = 1;

    /// <summary>Cap on a stored relay name (a Ward against an oversized name in loaded/wire data).</summary>
    public const int MaxNameLength = 64;

    /// <summary>Cap on the number of stored relays (a Ward against an oversized store).</summary>
    public const int MaxEntries = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<Sigil, KnownRelay> _bySigil = new();

    /// <summary>
    /// Classifies a relay about to be used. If <paramref name="name"/> is given and a *different* Sigil already
    /// holds that name, returns <see cref="RelayTrust.NameConflict"/> (impersonation) — this takes precedence over
    /// New/Known so a changed key is never silently accepted.
    /// </summary>
    public RelayTrust Evaluate(Sigil sigil, string? name = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(name))
            {
                foreach (var entry in _bySigil.Values)
                    if (entry.Name == name && entry.Sigil != sigil)
                        return RelayTrust.NameConflict;
            }
            return _bySigil.ContainsKey(sigil) ? RelayTrust.Known : RelayTrust.New;
        }
    }

    /// <summary>Whether this relay key has been approved.</summary>
    public bool IsApproved(Sigil sigil)
    {
        lock (_gate)
            return _bySigil.ContainsKey(sigil);
    }

    /// <summary>Approves a relay (or refreshes its last‑seen time and name). Call after the user consents.</summary>
    public void Approve(Sigil sigil, string? name, DateTimeOffset now)
    {
        var ts = now.ToUnixTimeSeconds();
        lock (_gate)
        {
            if (_bySigil.TryGetValue(sigil, out var existing))
                _bySigil[sigil] = existing with { Name = Trim(name) ?? existing.Name, LastSeenUnix = ts };
            else if (_bySigil.Count < MaxEntries)
                _bySigil[sigil] = new KnownRelay(sigil, Trim(name), ts, ts);
        }
    }

    /// <summary>Forgets a relay (it will prompt again next time). Returns whether it was present.</summary>
    public bool Remove(Sigil sigil)
    {
        lock (_gate)
            return _bySigil.Remove(sigil);
    }

    /// <summary>A snapshot of all approved relays.</summary>
    public IReadOnlyList<KnownRelay> All()
    {
        lock (_gate)
            return _bySigil.Values.ToList();
    }

    /// <summary>Serializes the store for the app to persist (e.g. in its encrypted secret store).</summary>
    public byte[] Encode()
    {
        lock (_gate)
        {
            var w = new CodexWriter();
            w.WriteByte(Version);
            w.WriteVarUInt((ulong)_bySigil.Count);
            foreach (var entry in _bySigil.Values)
            {
                w.WriteBytes(entry.Sigil.Span);
                w.WriteString(entry.Name ?? string.Empty);
                w.WriteUInt64((ulong)entry.FirstSeenUnix);
                w.WriteUInt64((ulong)entry.LastSeenUnix);
            }
            return w.ToArray();
        }
    }

    /// <summary>Reconstructs a store from <see cref="Encode"/> bytes. Malformed or empty input yields an empty store.</summary>
    public static KnownRelays Decode(ReadOnlySpan<byte> data)
    {
        var store = new KnownRelays();
        if (data.Length == 0)
            return store;
        try
        {
            var r = new CodexReader(data);
            if (r.ReadByte() != Version)
                return store;
            var count = r.ReadVarUInt();
            if (count > MaxEntries)
                return new KnownRelays();
            for (var i = 0UL; i < count; i++)
            {
                var sigilBytes = r.ReadBytes();
                if (sigilBytes.Length != Sigil.Size)
                    break;
                var sigil = new Sigil(sigilBytes);
                var name = r.ReadString();
                var first = (long)r.ReadUInt64();
                var last = (long)r.ReadUInt64();
                store._bySigil[sigil] = new KnownRelay(sigil, string.IsNullOrEmpty(name) ? null : name, first, last);
            }
        }
        catch (CodexFormatException)
        {
            // Truncated/garbage store — return whatever parsed cleanly rather than throwing at the caller.
        }
        return store;
    }

    private static string? Trim(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }
}
