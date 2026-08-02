using System.Globalization;
using System.Text;

namespace CupriNet.Core;

/// <summary>
/// A Moniker is a node's self-asserted display name (e.g. "Community Relay"). It rides inside a signed body, so a node
/// can only claim one for its own key — but CupriNet <b>never verifies it</b>: it is a display hint, and a consuming
/// app decides whether to believe it, always by matching the node's fingerprint (Sigil). Trust is entirely the
/// client's business; this helper only bounds abuse of the raw string.
///
/// <para><see cref="Normalize"/> is the single sanitiser, applied on both encode and decode, so a hostile value on the
/// wire is cleaned the moment it is read. It: strips control, format (zero-width / bidi-override), separator, private-
/// use, surrogate and unassigned code points (which enable spoofing or wreck a terminal/UI); collapses any run of
/// whitespace to one space and trims the ends; and clamps to <see cref="MaxLength"/> runes (never splitting a
/// surrogate pair). Over-long is clamped, not rejected — a cosmetic field must never sink the whole link/record.</para>
/// </summary>
public static class Monikers
{
    /// <summary>Max Moniker length accepted (in runes/text elements), to bound a malicious label (a Ward).</summary>
    public const int MaxLength = 48;

    /// <summary>Sanitises and bounds a Moniker: null/blank -> null; otherwise cleaned and clamped as described above.</summary>
    public static string? Normalize(string? moniker)
    {
        if (string.IsNullOrWhiteSpace(moniker))
            return null;

        var sb = new StringBuilder(MaxLength);
        var count = 0;
        var pendingSpace = false;

        foreach (var rune in moniker.EnumerateRunes())
        {
            // Fold any whitespace (including exotic Unicode spaces / newlines) into a single space, and never lead
            // with one. The space is only emitted once a following visible rune is seen, so ends can't be spaced.
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            // Drop anything that isn't safe, visible text: control (Cc), format — zero-width, RLO/LRO bidi overrides
            // (Cf), surrogates (Cs), private-use (Co), separators (Zl/Zp/Zs), and unassigned (Cn) code points.
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.SpaceSeparator:
                    continue;
            }

            if (count >= MaxLength)
                break;

            if (pendingSpace)
            {
                sb.Append(' ');
                count++;
                pendingSpace = false;
                if (count >= MaxLength)
                    break;
            }

            sb.Append(rune.ToString());
            count++;
        }

        // TrimEnd covers the one edge where a pending space was emitted as the final clamped element.
        var result = sb.ToString().TrimEnd();
        return result.Length == 0 ? null : result;
    }
}
