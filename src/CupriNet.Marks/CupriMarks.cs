using CupriMark;

namespace CupriNet.Marks;

/// <summary>
/// CupriNet's CupriMark catalogue: the immutable, hashed registry of every negotiable protocol
/// component and the versions each release speaks. On the wire peers exchange only an ordinal range per
/// component; each side resolves the agreed ordinal against this same built-in catalogue. Adding a
/// capability is an append here (a new <see cref="ComponentVersion"/>) plus the code path that handles
/// it — nodes then range-negotiate the highest common version instead of hard-failing on an equality
/// check, so a newer build keeps talking to an older one through a deliberate, security-aware window.
/// </summary>
public static class CupriMarks
{
    /// <summary>The Conjunction (L1 transport pairing handshake) protocol component.</summary>
    public const string Conjunction = "conjunction";

    /// <summary>The Consecration (L2 Arcanum channel handshake) protocol component.</summary>
    public const string Consecration = "consecration";

    /// <summary>The Decree (signed channel advertisement) document component.</summary>
    public const string Decree = "decree";

    /// <summary>
    /// The single built-in catalogue for the CupriNet protocol suite. Frozen at first access; its
    /// SHA-256 <see cref="Catalogue.Id"/> identifies exactly this set of definitions, so two builds that
    /// share an ordinal resolve it to identical behaviour without trusting each other.
    /// </summary>
    public static Catalogue Catalogue { get; } = Build();

    private static Catalogue Build() => CupriMark.Catalogue.Create("cuprinet",
    [
        new Component(Conjunction,
        [
            // v1 — the Noise_XX transport handshake with an Ed25519 identity binding over the Noise
            // handshake hash (the original, and so far only, pairing protocol).
            new ComponentVersion(1, BumpReason.Functionality, VersionStatus.Active),
        ]),
        new Component(Consecration,
        [
            // v1 — the Watchword key-confirmation channel handshake, transcript-bound to the channel
            // Ascendant, epoch, both peer Sigils, and both nonces.
            new ComponentVersion(1, BumpReason.Functionality, VersionStatus.Active),
        ]),
        new Component(Decree,
        [
            // v1 — the signed, epoch-scoped channel advertisement document.
            new ComponentVersion(1, BumpReason.Functionality, VersionStatus.Active),
        ]),
    ]);

    /// <summary>The ordinal range this build advertises for <paramref name="component"/> (what a peer negotiates against).</summary>
    public static OrdinalRange Supported(string component) => Require(component).Supported;

    /// <summary>
    /// Negotiates the highest mutually-supported ordinal for <paramref name="component"/> against a peer's
    /// advertised range, clamped to this build's security floor. Inspect <see cref="NegotiationResult.Accepted"/>.
    /// </summary>
    public static NegotiationResult Negotiate(string component, OrdinalRange peerAdvertised) =>
        Negotiator.Negotiate(Require(component), peerAdvertised);

    /// <summary>
    /// Whether this build accepts a one-way document/message stamped at <paramref name="version"/> for
    /// <paramref name="component"/> — i.e. it is a version we support, at or above our security floor, and
    /// not buried. Equivalent to negotiating against a peer that speaks only that single version, so it
    /// reuses the exact floor/lifecycle rules: a too-new version (above our max) or a superseded/buried one
    /// is refused. Use this for versioned documents (a Decree, a link) that carry no negotiation exchange.
    /// </summary>
    public static bool Accepts(string component, int version) =>
        version is >= 0 and <= ushort.MaxValue
        && Negotiator.Negotiate(Require(component), OrdinalRange.Create((ushort)version, (ushort)version)).Accepted;

    private static Component Require(string component) =>
        Catalogue.Component(component)
        ?? throw new ArgumentException($"Unknown CupriNet catalogue component '{component}'.", nameof(component));
}
