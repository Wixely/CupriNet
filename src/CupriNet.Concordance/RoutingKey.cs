using System.Security.Cryptography;

namespace CupriNet.Concordance;

/// <summary>
/// A node's (or lookup target's) uniformly-distributed routing coordinate — the Ascendant. Derived by
/// hashing so the keyspace is even; overlay routing walks XOR distance in this space. Distinct from the
/// Sigil by a domain separator, so routing position is not the same value as identity.
/// </summary>
public readonly struct RoutingKey : IEquatable<RoutingKey>
{
    public const int Size = 32;

    private static readonly byte[] AscendantDomain = "cuprinet/ascendant/v1"u8.ToArray();

    private readonly byte[]? _bytes;

    public RoutingKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"A RoutingKey must be {Size} bytes.", nameof(bytes));
        _bytes = bytes.ToArray();
    }

    public ReadOnlySpan<byte> Span => _bytes ?? new byte[Size];

    /// <summary>The Ascendant of a node identified by its Seal public key.</summary>
    public static RoutingKey FromSealPublicKey(ReadOnlySpan<byte> sealPublicKey)
    {
        var buffer = new byte[AscendantDomain.Length + sealPublicKey.Length];
        AscendantDomain.CopyTo(buffer, 0);
        sealPublicKey.CopyTo(buffer.AsSpan(AscendantDomain.Length));
        return new RoutingKey(SHA256.HashData(buffer));
    }

    /// <summary>The routing coordinate of an opaque lookup token (e.g. a channel Glyph).</summary>
    public static RoutingKey FromToken(ReadOnlySpan<byte> token) => new(SHA256.HashData(token));

    /// <summary>The XOR distance between this key and another (smaller = closer).</summary>
    public XorDistance DistanceTo(RoutingKey other)
    {
        var a = Span;
        var b = other.Span;
        var distance = new byte[Size];
        for (var i = 0; i < Size; i++)
            distance[i] = (byte)(a[i] ^ b[i]);
        return new XorDistance(distance);
    }

    public bool Equals(RoutingKey other) => Span.SequenceEqual(other.Span);

    public override bool Equals(object? obj) => obj is RoutingKey other && Equals(other);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.AddBytes(Span);
        return hc.ToHashCode();
    }

    public override string ToString() => Convert.ToHexStringLower(Span);
}

/// <summary>An XOR distance in the routing keyspace, ordered as a 256-bit big-endian magnitude.</summary>
public readonly struct XorDistance : IComparable<XorDistance>, IEquatable<XorDistance>
{
    private readonly byte[] _distance;

    internal XorDistance(byte[] distance) => _distance = distance;

    public int CompareTo(XorDistance other)
    {
        var a = _distance;
        var b = other._distance;
        for (var i = 0; i < RoutingKey.Size; i++)
        {
            if (a[i] != b[i])
                return a[i] < b[i] ? -1 : 1;
        }

        return 0;
    }

    public bool Equals(XorDistance other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is XorDistance other && Equals(other);

    public override int GetHashCode()
    {
        if (_distance is null)
            return 0;
        var hc = new HashCode();
        hc.AddBytes(_distance);
        return hc.ToHashCode();
    }

    public static bool operator <(XorDistance left, XorDistance right) => left.CompareTo(right) < 0;

    public static bool operator >(XorDistance left, XorDistance right) => left.CompareTo(right) > 0;

    public static bool operator <=(XorDistance left, XorDistance right) => left.CompareTo(right) <= 0;

    public static bool operator >=(XorDistance left, XorDistance right) => left.CompareTo(right) >= 0;
}
