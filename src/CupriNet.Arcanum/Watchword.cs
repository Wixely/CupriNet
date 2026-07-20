using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace CupriNet.Arcanum;

/// <summary>
/// A channel code: <c>Appellation#Obscuration</c>. The Appellation is a human-readable name (never
/// containing '#'); the Obscuration is a random salt (≥16 bytes, Base64Url-encoded). Both parts are
/// mandatory. Security rests entirely on the Obscuration — the name is cosmetic. Anyone holding the full
/// Watchword can discover the channel and attempt the Consecration handshake, so it is a shared secret.
/// </summary>
public sealed class Watchword
{
    /// <summary>Minimum salt length: 128 bits.</summary>
    public const int MinObscurationBytes = 16;

    /// <summary>Upper bound on the (normalized) name length.</summary>
    public const int MaxAppellationLength = 256;

    private Watchword(string appellation, byte[] obscuration)
    {
        Appellation = appellation;
        Obscuration = obscuration;
    }

    /// <summary>The human-readable channel name (Unicode NFC, no '#').</summary>
    public string Appellation { get; }

    /// <summary>The random salt.</summary>
    public byte[] Obscuration { get; }

    /// <summary>Creates a Watchword for a name with a fresh random Obscuration.</summary>
    public static Watchword Generate(string appellation, int obscurationBytes = MinObscurationBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(appellation);
        ArgumentOutOfRangeException.ThrowIfLessThan(obscurationBytes, MinObscurationBytes);

        var normalized = Normalize(appellation);
        if (!IsValidAppellation(normalized))
            throw new ArgumentException("Appellation must be non-empty and must not contain '#'.", nameof(appellation));

        return new Watchword(normalized, RandomNumberGenerator.GetBytes(obscurationBytes));
    }

    /// <summary>Parses a <c>name#salt</c> code. Returns false for any malformed or under-length input.</summary>
    public static bool TryParse(string code, out Watchword watchword)
    {
        watchword = null!;
        if (string.IsNullOrEmpty(code))
            return false;

        var separator = code.LastIndexOf('#');
        if (separator <= 0 || separator == code.Length - 1)
            return false;

        var name = code[..separator];
        var saltText = code[(separator + 1)..];
        if (name.Contains('#')) // more than one separator => ambiguous
            return false;

        byte[] salt;
        try
        {
            salt = Base64Url.DecodeFromChars(saltText);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length < MinObscurationBytes)
            return false;

        var normalized = Normalize(name);
        if (!IsValidAppellation(normalized))
            return false;

        watchword = new Watchword(normalized, salt);
        return true;
    }

    /// <summary>Renders the canonical <c>name#salt</c> form.</summary>
    public override string ToString() => $"{Appellation}#{Base64Url.EncodeToString(Obscuration)}";

    private static string Normalize(string name) => name.Normalize(NormalizationForm.FormC);

    private static bool IsValidAppellation(string normalized)
        => normalized.Length is > 0 and <= MaxAppellationLength && !normalized.Contains('#');
}
