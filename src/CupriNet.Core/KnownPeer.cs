using CupriNet.Abstractions;
using CupriNet.Codex;
using CupriNet.Marks;

namespace CupriNet.Core;

/// <summary>
/// A peer we have previously paired and Consecrated a channel with — a trusted contact we can re-dial
/// directly on a later run, without a fresh Intonation. Its dialable <see cref="Beacons"/> are the seed
/// we cached; its <see cref="Sigil"/> is pinned so a reconnect refuses any other identity at that address.
/// This is the storable form of the "route back to people we trust immediately" philosophy.
/// </summary>
public sealed record KnownPeer
{
    public required Sigil Sigil { get; init; }
    public required byte[] SealPublicKey { get; init; }
    public required IReadOnlyList<Beacon> Beacons { get; init; }
    public required long LastSeenUnix { get; init; }
}

/// <summary>Canonical serialization for <see cref="KnownPeer"/>.</summary>
public static class KnownPeerCodec
{
    /// <summary>Maximum beacons cached per known peer.</summary>
    public const int MaxBeacons = 16;

    public static byte[] Encode(KnownPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var w = new CodexWriter();
        // Stamp the highest KnownPeer version this build supports (per the CupriMark catalogue); a reader accepts
        // it via CupriMarks.Accepts, so a future format bump range-negotiates instead of hard-failing on equality.
        w.WriteByte(checked((byte)CupriMarks.Supported(CupriMarks.KnownPeer).Max));
        w.WriteBytes(peer.SealPublicKey);
        BeaconCodec.Write(w, peer.Beacons, MaxBeacons);
        w.WriteUInt64((ulong)peer.LastSeenUnix);
        return w.ToArray();
    }

    public static KnownPeer Decode(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        var version = r.ReadByte();
        if (!CupriMarks.Accepts(CupriMarks.KnownPeer, version))
            throw new CodexFormatException($"Unsupported KnownPeer version {version}.");
        var sealKey = r.ReadBytes().ToArray();
        if (sealKey.Length is 0 or > 64)
            throw new CodexFormatException("Invalid Seal public key length.");
        var beacons = BeaconCodec.Read(ref r, MaxBeacons);
        var lastSeen = (long)r.ReadUInt64();
        return new KnownPeer
        {
            Sigil = Sigil.FromSealPublicKey(sealKey),
            SealPublicKey = sealKey,
            Beacons = beacons,
            LastSeenUnix = lastSeen,
        };
    }
}
