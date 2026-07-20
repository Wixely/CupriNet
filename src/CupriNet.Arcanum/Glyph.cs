using System.Buffers.Binary;
using CupriNet.Alembic;

namespace CupriNet.Arcanum;

/// <summary>
/// The rotating public lookup token for an Arcanum. Only holders of the Watchword (hence the Glyph key)
/// can compute it, and it rotates every Turning, so a passive observer cannot correlate a channel by one
/// fixed value over time. Members compute the current and adjacent-epoch Glyphs to tolerate clock skew.
/// </summary>
public static class Glyph
{
    /// <summary>Default rotation period: 10 minutes.</summary>
    public const long DefaultTurningSeconds = 600;

    private const int GlyphLength = 32;
    private static readonly byte[] Info = "cuprinet/arcanum/glyph-token/v1"u8.ToArray();

    /// <summary>The epoch (Cycle) index for a moment in time.</summary>
    public static long Epoch(DateTimeOffset now, long turningSeconds = DefaultTurningSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(turningSeconds);
        return now.ToUnixTimeSeconds() / turningSeconds;
    }

    /// <summary>Computes the Glyph for a specific epoch.</summary>
    public static byte[] ForEpoch(ArcanumKeys keys, long epoch, ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(suite);

        Span<byte> epochBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(epochBytes, epoch);
        return suite.Kdf.DeriveKey(keys.GlyphKey, epochBytes, Info, GlyphLength);
    }

    /// <summary>The Glyph for the current epoch.</summary>
    public static byte[] Current(ArcanumKeys keys, DateTimeOffset now, ICryptoSuite suite, long turningSeconds = DefaultTurningSeconds)
        => ForEpoch(keys, Epoch(now, turningSeconds), suite);

    /// <summary>The previous, current, and next epoch Glyphs — the acceptance window for clock skew.</summary>
    public static IReadOnlyList<byte[]> Window(ArcanumKeys keys, DateTimeOffset now, ICryptoSuite suite, long turningSeconds = DefaultTurningSeconds)
    {
        var epoch = Epoch(now, turningSeconds);
        return [ForEpoch(keys, epoch - 1, suite), ForEpoch(keys, epoch, suite), ForEpoch(keys, epoch + 1, suite)];
    }
}
