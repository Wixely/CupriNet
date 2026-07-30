using CupriNet.Abstractions;
using CupriNet.Codex;

namespace CupriNet.Core;

/// <summary>
/// Canonical serialization for <see cref="Intonation"/>. The document is an envelope of two
/// length-prefixed byte strings: the canonical body, then the signature. The body is what gets signed,
/// so it can be re-extracted verbatim on parse and verified against the embedded Seal public key.
/// </summary>
public static class IntonationCodec
{
    /// <summary>The current Intonation document version.</summary>
    public const byte CurrentVersion = 1;

    /// <summary>Max beacons/seed records accepted, to bound a malicious link (a Ward).</summary>
    public const int MaxBeacons = 32;

    /// <summary>Max seed Sigils in the Litany roll.</summary>
    public const int MaxLitany = 32;

    /// <summary>Encodes the canonical, signable body (everything except the signature).</summary>
    public static byte[] EncodeBody(Intonation intonation)
    {
        ArgumentNullException.ThrowIfNull(intonation);

        var w = new CodexWriter();
        w.WriteByte(intonation.Version);
        w.WriteString(intonation.Network.Value);
        w.WriteBytes(intonation.InviterSealPublicKey);

        BeaconCodec.Write(w, intonation.Beacons, MaxBeacons);

        w.WriteVarUInt((ulong)intonation.Litany.Count);
        foreach (var sigil in intonation.Litany)
            w.WriteBytes(sigil.Span);

        w.WriteUInt64((ulong)intonation.IssuedAtUnix);
        w.WriteUInt64((ulong)intonation.SeveranceUnix);
        w.WriteBytes(intonation.Nonce);

        if (intonation.Petition is { } petition)
        {
            w.WriteByte(1);
            w.WriteBytes(petition);
        }
        else
        {
            w.WriteByte(0);
        }

        // Optional trailing Moniker: written only when set, so a link without one is byte-identical to the older
        // format (backward-compatible — old decoders read up to here and stop; see DecodeBody).
        var moniker = Monikers.Normalize(intonation.Moniker);
        if (moniker is not null)
            w.WriteString(moniker);

        return w.ToArray();
    }

    /// <summary>Encodes the full signed document (body envelope + signature).</summary>
    public static byte[] Encode(Intonation intonation)
    {
        ArgumentNullException.ThrowIfNull(intonation);
        var w = new CodexWriter();
        w.WriteBytes(EncodeBody(intonation));
        w.WriteBytes(intonation.Signature);
        return w.ToArray();
    }

    /// <summary>
    /// Decodes a full document, returning the parsed value and the exact body bytes that were signed
    /// (needed to verify the signature).
    /// </summary>
    public static (Intonation Intonation, byte[] SignedBody) Decode(ReadOnlySpan<byte> document)
    {
        var reader = new CodexReader(document);
        var body = reader.ReadBytes().ToArray();
        var signature = reader.ReadBytes().ToArray();
        var intonation = DecodeBody(body) with { Signature = signature };
        return (intonation, body);
    }

    private static Intonation DecodeBody(ReadOnlySpan<byte> body)
    {
        var r = new CodexReader(body);

        var version = r.ReadByte();
        var network = new Concordium(r.ReadString());
        var inviterKey = r.ReadBytes().ToArray();

        var beacons = BeaconCodec.Read(ref r, MaxBeacons);

        var litanyCount = r.ReadVarUInt();
        if (litanyCount > MaxLitany)
            throw new CodexFormatException($"Intonation Litany has {litanyCount} entries, exceeding the maximum of {MaxLitany}.");
        var litany = new List<Sigil>();
        for (var i = 0UL; i < litanyCount; i++)
        {
            var sigilBytes = r.ReadBytes();
            if (sigilBytes.Length != Sigil.Size)
                throw new CodexFormatException("Litany entry has an invalid Sigil length.");
            litany.Add(new Sigil(sigilBytes));
        }

        var issuedAt = (long)r.ReadUInt64();
        var severance = (long)r.ReadUInt64();
        var nonce = r.ReadBytes().ToArray();

        var hasPetition = r.ReadByte();
        byte[]? petition = hasPetition switch
        {
            0 => null,
            1 => r.ReadBytes().ToArray(),
            _ => throw new CodexFormatException("Invalid Petition presence flag."),
        };

        // Optional trailing Moniker (see EncodeBody): present only if bytes remain. Over-long is clamped rather
        // than rejected — it's a cosmetic hint, never trusted, so a malformed one must not sink the whole link.
        var moniker = r.End ? null : Monikers.Normalize(r.ReadString());

        return new Intonation
        {
            Version = version,
            Network = network,
            InviterSealPublicKey = inviterKey,
            Beacons = beacons,
            Litany = litany,
            IssuedAtUnix = issuedAt,
            SeveranceUnix = severance,
            Nonce = nonce,
            Petition = petition,
            Moniker = moniker,
            Signature = [],
        };
    }
}
