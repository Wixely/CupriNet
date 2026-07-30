namespace CupriNet.Core;

/// <summary>
/// A Moniker is a node's self-asserted display name (e.g. "Community Relay"). It rides inside a signed body, so a node can
/// only claim one for its own key — but CupriNet <b>never verifies it</b>: it is a display hint, and a consuming app
/// decides whether to believe it, always by matching the node's fingerprint (Sigil). This helper only normalises the
/// string (trim + length Ward); trust is entirely the client's business.
/// </summary>
public static class Monikers
{
    /// <summary>Max Moniker length accepted, to bound a malicious label (a Ward). Over-long is clamped, not rejected.</summary>
    public const int MaxLength = 48;

    /// <summary>Normalises a Moniker: null/blank -> null, trimmed, and clamped to <see cref="MaxLength"/>.</summary>
    public static string? Normalize(string? moniker)
    {
        if (string.IsNullOrWhiteSpace(moniker))
            return null;
        moniker = moniker.Trim();
        return moniker.Length > MaxLength ? moniker[..MaxLength] : moniker;
    }
}
