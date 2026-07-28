using System.Security.Cryptography;

namespace CupriNet.Alembic;

/// <summary>
/// SHA-2 hashing via the .NET BCL. Hashing is not a secrecy boundary, so this real implementation is
/// used even by the insecure Simulacrum — a Sigil must be a genuine SHA-256 from day one.
/// </summary>
public sealed class BclHashProvider : IHashProvider
{
    public byte[] Sha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    public byte[] Sha512(ReadOnlySpan<byte> data) => SHA512.HashData(data);
}

/// <summary>
/// HKDF-SHA256 (RFC 5869) implemented directly over HMAC-SHA256. We do NOT use <c>HKDF.DeriveKey</c>: its
/// OpenSSL-backed path on Linux rejects <em>empty</em> input key material, whereas Windows/CNG accepts it — and
/// Noise's <c>Split()</c> derives its transport keys with an empty IKM, so relying on the BCL HKDF makes every
/// handshake fail on Linux. HMAC over an empty message is well-defined and portable, so this is correct
/// everywhere and produces the exact RFC 5869 output.
/// </summary>
public sealed class BclHkdf : IKdf
{
    private const int HashLen = 32; // SHA-256

    public byte[] DeriveKey(ReadOnlySpan<byte> inputKeyMaterial, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 255 * HashLen);

        // Extract: PRK = HMAC-SHA256(salt, IKM). An empty salt is treated as HashLen zero bytes (RFC 5869 §2.2).
        var saltKey = salt.IsEmpty ? new byte[HashLen] : salt.ToArray();
        var prk = HMACSHA256.HashData(saltKey, inputKeyMaterial);

        // Expand: T(0) = empty; T(i) = HMAC-SHA256(PRK, T(i-1) || info || i); OKM = T(1) || T(2) || … (RFC 5869 §2.3).
        var okm = new byte[length];
        Span<byte> block = stackalloc byte[HashLen];
        var blockLength = 0;
        var produced = 0;
        byte counter = 1;
        while (produced < length)
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, prk);
            hmac.AppendData(block[..blockLength]);
            hmac.AppendData(info);
            hmac.AppendData([counter]);
            hmac.GetHashAndReset(block);
            blockLength = HashLen;

            var take = Math.Min(HashLen, length - produced);
            block[..take].CopyTo(okm.AsSpan(produced));
            produced += take;
            counter++;
        }
        return okm;
    }
}
