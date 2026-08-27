namespace CupriNet.Rites;

/// <summary>
/// The size-quantisation shared by the rites that pad (see <see cref="AuspicePadding"/>, <see cref="ConduitPadding"/>).
/// One implementation so the two policies cannot drift; the ceiling differs per rite, so it is passed in rather than
/// baked in.
/// </summary>
internal static class PaddingMath
{
    /// <summary>The smallest bucket a powers-of-two policy rounds up to.</summary>
    internal const int MinPowerOfTwoBucket = 256;

    /// <summary>
    /// Rounds <paramref name="length"/> up under the given policy, never past <paramref name="ceiling"/> — quantising
    /// a size must never grow a message into one the transport would refuse, so an at-limit frame stays as it is.
    /// </summary>
    internal static int Quantise(int length, int block, bool powerOfTwo, int ceiling)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        var target = (powerOfTwo, block) switch
        {
            (true, _) => (int)Math.Min(
                (uint)ceiling,
                System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(length, MinPowerOfTwoBucket))),
            (false, > 0) => length % block == 0 ? length : length + (block - (length % block)),
            _ => length,
        };

        return Math.Clamp(target, length, Math.Max(length, ceiling));
    }
}
