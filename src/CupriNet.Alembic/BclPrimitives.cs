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

/// <summary>HKDF-SHA256 via the .NET BCL.</summary>
public sealed class BclHkdf : IKdf
{
    public byte[] DeriveKey(ReadOnlySpan<byte> inputKeyMaterial, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var output = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, output, salt, info);
        return output;
    }
}
