using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace CupriNet.Concordance;

/// <summary>
/// Tribute — a Hashcash-style proof of work used to price abusive actions (chiefly channel-advertisement
/// spam). A solver grinds a nonce so that <c>SHA-256(domain ‖ subject ‖ nonce)</c> has at least a required
/// number of leading zero bits; a verifier confirms it with a single hash. The subject binds the proof to a
/// specific action (e.g. one Decree), so it cannot be transplanted. Difficulty is a <em>local receiver
/// policy</em> (each node requires what it wants) and is hard-capped, so no party can be forced to grind an
/// unbounded amount — the payer always knows the ceiling.
/// </summary>
public static class Tribute
{
    private static readonly byte[] Domain = "cuprinet/tribute/v1"u8.ToArray();

    /// <summary>Hard cap on difficulty (bits). Bounds both what a solver will grind and what a verifier may demand.</summary>
    public const int MaxDifficulty = 24;

    /// <summary>Nonce size (a big-endian counter).</summary>
    private const int NonceSize = 8;

    /// <summary>
    /// Grinds a nonce whose digest over <paramref name="subject"/> has at least <paramref name="difficulty"/>
    /// leading zero bits (clamped to <see cref="MaxDifficulty"/>, so the work is always bounded).
    /// </summary>
    public static byte[] Solve(ReadOnlySpan<byte> subject, int difficulty, CancellationToken cancellationToken = default)
    {
        difficulty = Math.Clamp(difficulty, 0, MaxDifficulty);

        var buffer = new byte[Domain.Length + subject.Length + NonceSize];
        Domain.CopyTo(buffer, 0);
        subject.CopyTo(buffer.AsSpan(Domain.Length));
        var nonce = buffer.AsSpan(Domain.Length + subject.Length, NonceSize);

        Span<byte> hash = stackalloc byte[32];
        for (ulong counter = 0; ; counter++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(nonce, counter);
            SHA256.HashData(buffer, hash);
            if (LeadingZeroBits(hash) >= difficulty)
                return nonce.ToArray();
            if ((counter & 0xFFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Verifies a nonce meets a required difficulty over the subject (a single hash — O(1)).</summary>
    public static bool Verify(ReadOnlySpan<byte> subject, ReadOnlySpan<byte> nonce, int requiredDifficulty)
    {
        requiredDifficulty = Math.Clamp(requiredDifficulty, 0, MaxDifficulty);
        if (nonce.Length is 0 or > 64)
            return false;

        var buffer = new byte[Domain.Length + subject.Length + nonce.Length];
        Domain.CopyTo(buffer, 0);
        subject.CopyTo(buffer.AsSpan(Domain.Length));
        nonce.CopyTo(buffer.AsSpan(Domain.Length + subject.Length));

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);
        return LeadingZeroBits(hash) >= requiredDifficulty;
    }

    /// <summary>Counts the leading zero bits of a hash.</summary>
    public static int LeadingZeroBits(ReadOnlySpan<byte> hash)
    {
        var bits = 0;
        foreach (var b in hash)
        {
            if (b == 0)
            {
                bits += 8;
                continue;
            }
            bits += BitOperations.LeadingZeroCount((uint)b) - 24; // leading zeros within this byte
            break;
        }
        return bits;
    }
}
