using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;
using CupriNet.Core;

namespace CupriNet.Arcanum;

/// <summary>
/// A signed, short-lived advertisement that a provider keeps a particular Arcanum. It is opaque to
/// observers: it carries only the rotating Glyph (never the Watchword or the stable Ascendant), the
/// provider identity, and reachability. Placed at nodes near the channel's Ascendant and matched by the
/// current Glyph, so only Watchword-holders can recognise it, and only for the current epoch window.
/// </summary>
public sealed record Decree
{
    public required byte Version { get; init; }
    public required byte[] Glyph { get; init; }
    public required byte[] ProviderSealPublicKey { get; init; }
    public required IReadOnlyList<Beacon> Endpoints { get; init; }
    public required long ExpiresAtUnix { get; init; }
    public required ulong SequenceNumber { get; init; }
    public required byte[] Signature { get; init; }

    public Sigil ProviderSigil => Sigil.FromSealPublicKey(ProviderSealPublicKey);
}

/// <summary>Canonical serialization for <see cref="Decree"/> (signed body + signature envelope).</summary>
public static class DecreeCodec
{
    public const byte CurrentVersion = 1;
    public const int MaxEndpoints = 8;

    public static byte[] EncodeBody(Decree decree)
    {
        ArgumentNullException.ThrowIfNull(decree);
        var w = new CodexWriter();
        w.WriteByte(decree.Version);
        w.WriteBytes(decree.Glyph);
        w.WriteBytes(decree.ProviderSealPublicKey);
        BeaconCodec.Write(w, decree.Endpoints, MaxEndpoints);
        w.WriteUInt64((ulong)decree.ExpiresAtUnix);
        w.WriteUInt64(decree.SequenceNumber);
        return w.ToArray();
    }

    public static byte[] Encode(Decree decree)
    {
        ArgumentNullException.ThrowIfNull(decree);
        var w = new CodexWriter();
        w.WriteBytes(EncodeBody(decree));
        w.WriteBytes(decree.Signature);
        return w.ToArray();
    }

    public static (Decree Decree, byte[] SignedBody) Decode(ReadOnlySpan<byte> document)
    {
        var reader = new CodexReader(document);
        var body = reader.ReadBytes().ToArray();
        var signature = reader.ReadBytes().ToArray();
        return (DecodeBody(body) with { Signature = signature }, body);
    }

    private static Decree DecodeBody(ReadOnlySpan<byte> body)
    {
        var r = new CodexReader(body);
        var version = r.ReadByte();
        var glyph = r.ReadBytes().ToArray();
        var providerKey = r.ReadBytes().ToArray();
        var endpoints = BeaconCodec.Read(ref r, MaxEndpoints);
        var expiresAt = (long)r.ReadUInt64();
        var sequence = r.ReadUInt64();

        return new Decree
        {
            Version = version,
            Glyph = glyph,
            ProviderSealPublicKey = providerKey,
            Endpoints = endpoints,
            ExpiresAtUnix = expiresAt,
            SequenceNumber = sequence,
            Signature = [],
        };
    }
}

/// <summary>Creates and verifies Decrees.</summary>
public static class DecreeSigner
{
    /// <summary>Signs a Decree for an explicit Glyph.</summary>
    public static Decree Create(NodeIdentity provider, byte[] glyph, IReadOnlyList<Beacon> endpoints, DateTimeOffset expiresAt, ulong sequenceNumber, ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(glyph);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(suite);

        var draft = new Decree
        {
            Version = DecreeCodec.CurrentVersion,
            Glyph = glyph,
            ProviderSealPublicKey = provider.PublicKey.ToArray(),
            Endpoints = endpoints,
            ExpiresAtUnix = expiresAt.ToUnixTimeSeconds(),
            SequenceNumber = sequenceNumber,
            Signature = [],
        };

        var signature = suite.CreateSigner(provider.Seal.PrivateKey).Sign(DecreeCodec.EncodeBody(draft));
        return draft with { Signature = signature };
    }

    /// <summary>Publishes a Decree for the current epoch's Glyph of a channel.</summary>
    public static Decree Publish(NodeIdentity provider, ArcanumKeys keys, IReadOnlyList<Beacon> endpoints, TimeSpan lifetime, ulong sequenceNumber, ICryptoSuite suite, DateTimeOffset now, long turningSeconds = Glyph.DefaultTurningSeconds)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var glyph = Glyph.Current(keys, now, suite, turningSeconds);
        return Create(provider, glyph, endpoints, now.Add(lifetime), sequenceNumber, suite);
    }

    /// <summary>Verifies a Decree's signature against the provider's Seal.</summary>
    public static bool Verify(Decree decree, ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(decree);
        ArgumentNullException.ThrowIfNull(suite);
        return suite.Verifier.Verify(DecreeCodec.EncodeBody(decree), decree.Signature, decree.ProviderSealPublicKey);
    }
}

/// <summary>Matches a Decree to a channel the caller holds the Watchword for.</summary>
public static class DecreeValidator
{
    /// <summary>True if the Decree is unexpired and its Glyph falls in the channel's current epoch window.</summary>
    public static bool Matches(Decree decree, ArcanumKeys keys, DateTimeOffset now, ICryptoSuite suite, long turningSeconds = Glyph.DefaultTurningSeconds)
    {
        ArgumentNullException.ThrowIfNull(decree);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(suite);

        if (now.ToUnixTimeSeconds() > decree.ExpiresAtUnix)
            return false;

        foreach (var glyph in Glyph.Window(keys, now, suite, turningSeconds))
        {
            if (glyph.AsSpan().SequenceEqual(decree.Glyph))
                return true;
        }

        return false;
    }
}
