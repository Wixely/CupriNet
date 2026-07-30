using CupriNet.Abstractions;

namespace CupriNet.Core;

/// <summary>
/// The full connection URL a node mints on demand (the "mesh-magnet" link). It is a signed snapshot of
/// the inviter's current reachability plus a sampled seed roll (Litany), and it expires by design.
/// </summary>
public sealed record Intonation
{
    /// <summary>Document format version.</summary>
    public required byte Version { get; init; }

    /// <summary>The network this Intonation belongs to.</summary>
    public required Concordium Network { get; init; }

    /// <summary>The inviter's Seal public key (also lets the holder derive the inviter Sigil).</summary>
    public required byte[] InviterSealPublicKey { get; init; }

    /// <summary>The inviter's reachability candidates.</summary>
    public required IReadOnlyList<Beacon> Beacons { get; init; }

    /// <summary>A sampled roll of seed peer Sigils (overlay entry points). May be empty in Phase 1.</summary>
    public required IReadOnlyList<Sigil> Litany { get; init; }

    /// <summary>Issue time (Unix seconds, UTC).</summary>
    public required long IssuedAtUnix { get; init; }

    /// <summary>Severance / expiry time (Unix seconds, UTC).</summary>
    public required long SeveranceUnix { get; init; }

    /// <summary>A random nonce making each Intonation unique (replay tracking).</summary>
    public required byte[] Nonce { get; init; }

    /// <summary>Optional capability secret (Petition) proving possession of the link.</summary>
    public byte[]? Petition { get; init; }

    /// <summary>
    /// Optional self-asserted display name (Moniker) the node claims for itself, e.g. "Wikipedia". Carried inside
    /// the signed body (so a node can only claim a Moniker for its own key), but <b>never verified by the protocol</b>
    /// — it is a display hint; a consuming client decides whether to believe it, always against the fingerprint.
    /// </summary>
    public string? Moniker { get; init; }

    /// <summary>Detached signature over the canonical body, made with the inviter's Seal.</summary>
    public required byte[] Signature { get; init; }

    /// <summary>The Sigil of the inviter, derived from the embedded Seal public key.</summary>
    public Sigil InviterSigil => Sigil.FromSealPublicKey(InviterSealPublicKey);
}
