using System.Text;
using CupriNet.Alembic;
using CupriNet.Concordance;

namespace CupriNet.Arcanum;

/// <summary>
/// The key schedule for an Arcanum, derived from its Watchword. The Crucible (Argon2id) hardens the
/// Watchword into the Materivane root key; Transmutation (HKDF) then splits the root into purpose-bound
/// subkeys. The routing subkey yields a <em>stable</em> Ascendant (where advertisements are placed),
/// while the Glyph subkey drives a <em>rotating</em> public lookup token — so routing geometry stays
/// fixed even as the on-wire identifier rotates.
/// </summary>
public sealed class ArcanumKeys
{
    private const int KeyLength = 32;

    private ArcanumKeys(RoutingKey ascendant, byte[] glyphKey, byte[] veilKey, byte[] concordKey)
    {
        Ascendant = ascendant;
        GlyphKey = glyphKey;
        VeilKey = veilKey;
        ConcordKey = concordKey;
    }

    /// <summary>Stable routing coordinate where the channel's advertisements are placed/replicated.</summary>
    public RoutingKey Ascendant { get; }

    /// <summary>Key for the rotating public lookup token (Glyph).</summary>
    public byte[] GlyphKey { get; }

    /// <summary>Key material for channel content encryption.</summary>
    public byte[] VeilKey { get; }

    /// <summary>Key material for channel-level authentication (Consecration binding).</summary>
    public byte[] ConcordKey { get; }

    /// <summary>Runs the Crucible and Transmutation to derive all channel keys from a Watchword.</summary>
    public static ArcanumKeys Derive(Watchword watchword, ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(watchword);
        ArgumentNullException.ThrowIfNull(suite);

        // Crucible: memory-hard hardening of the Appellation under the Obscuration salt -> Materivane.
        var materivane = suite.Passwords.Harden(
            Encoding.UTF8.GetBytes(watchword.Appellation), watchword.Obscuration, KeyLength);

        // Transmutation: HKDF-expand the root into purpose-bound subkeys.
        var ascendant = Subkey(suite, materivane, "cuprinet/arcanum/ascendant/v1");
        var glyphKey = Subkey(suite, materivane, "cuprinet/arcanum/glyph/v1");
        var veilKey = Subkey(suite, materivane, "cuprinet/arcanum/veil/v1");
        var concordKey = Subkey(suite, materivane, "cuprinet/arcanum/concord/v1");

        return new ArcanumKeys(new RoutingKey(ascendant), glyphKey, veilKey, concordKey);
    }

    private static byte[] Subkey(ICryptoSuite suite, byte[] root, string info)
        => suite.Kdf.DeriveKey(root, salt: [], info: Encoding.ASCII.GetBytes(info), KeyLength);
}
