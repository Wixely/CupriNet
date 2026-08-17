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

        // Optional trailing fields, in order: Moniker, the WebRTC block, then the Shrine address. All absent ⇒
        // byte-identical to the older format (old decoders read up to here and stop). Because they're positional, a
        // *later* field forces the earlier slots to be materialised — an empty Moniker string decodes back to null
        // (see DecodeBody), and an absent WebRTC block becomes the ShrineAddressCodec.AbsentBlock sentinel — so
        // "Shrine set, the rest unset" stays unambiguous.
        //
        // Interop: a link with a Shrine AND a WebRTC block still parses cleanly on a pre-Shrine build (it reads up to
        // the WebRTC block and ignores the rest). A Shrine with NO WebRTC block writes the 0 sentinel, which such a
        // build rejects as an unknown WebRTC version — so a public Shrine host should advertise both.
        var moniker = Monikers.Normalize(intonation.Moniker);
        var webrtc = intonation.WebRtc;
        var shrine = intonation.Shrine;
        if (moniker is not null || webrtc is not null || shrine is not null)
            w.WriteString(moniker ?? "");
        if (webrtc is not null)
            WebRtcCodec.Write(w, webrtc);
        else if (shrine is not null)
            w.WriteByte(ShrineAddressCodec.AbsentBlock);
        if (shrine is { } signet)
            ShrineAddressCodec.Write(w, signet);

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

        // Optional trailing fields (see EncodeBody), read positionally, each only if bytes remain: Moniker, the WebRTC
        // block, then the Shrine address. An over-long Moniker is clamped (cosmetic); a malformed WebRTC or Shrine
        // block is rejected (structural). The WebRTC slot's leading byte is read here so the 0 "absent" sentinel can
        // be told apart from a real block.
        var moniker = r.End ? null : Monikers.Normalize(r.ReadString());
        WebRtcEndpoint? webrtc = null;
        if (!r.End)
        {
            var block = r.ReadByte();
            if (block != ShrineAddressCodec.AbsentBlock)
                webrtc = WebRtcCodec.ReadAfterVersion(ref r, block);
        }
        Sigil? shrine = r.End ? null : ShrineAddressCodec.Read(ref r);

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
            WebRtc = webrtc,
            Shrine = shrine,
            Signature = [],
        };
    }
}
