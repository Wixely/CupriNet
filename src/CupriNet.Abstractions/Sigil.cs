using System.Security.Cryptography;

namespace CupriNet.Abstractions;

/// <summary>
/// A node's public mark: the SHA-256 of its long-term identity (Seal) public key.
/// This is the pseudonymous overlay identifier used throughout the Concordance.
/// </summary>
public readonly struct Sigil : IEquatable<Sigil>
{
    /// <summary>Length of a Sigil in bytes.</summary>
    public const int Size = 32;

    private readonly byte[]? _bytes;

    /// <summary>Wraps an existing 32-byte mark.</summary>
    public Sigil(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"A Sigil must be exactly {Size} bytes.", nameof(bytes));
        _bytes = bytes.ToArray();
    }

    /// <summary>Derives the Sigil for a Seal (identity) public key.</summary>
    public static Sigil FromSealPublicKey(ReadOnlySpan<byte> sealPublicKey)
        => new(SHA256.HashData(sealPublicKey));

    /// <summary>The raw 32 bytes. Empty for a default-constructed Sigil.</summary>
    public ReadOnlySpan<byte> Span => _bytes ?? [];

    /// <summary>True for a default (all-zero-length) Sigil.</summary>
    public bool IsEmpty => _bytes is null;

    public bool Equals(Sigil other) => Span.SequenceEqual(other.Span);

    public override bool Equals(object? obj) => obj is Sigil other && Equals(other);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.AddBytes(Span);
        return hc.ToHashCode();
    }

    /// <summary>Lower-case hex rendering of the full mark.</summary>
    public override string ToString() => IsEmpty ? "<empty>" : Convert.ToHexStringLower(Span);

    public static bool operator ==(Sigil left, Sigil right) => left.Equals(right);

    public static bool operator !=(Sigil left, Sigil right) => !left.Equals(right);
}
