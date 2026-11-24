using System.Net;
using System.Security.Cryptography;
using CupriNet.Codex;
using CupriNet.Core;
using CupriNet.Marks;

namespace CupriNet.Hosting;

/// <summary>
/// Wire format for the Ferryman rendezvous — the small signaling exchange a relay brokers so two NAT'd peers can
/// hole-punch a direct connection. The relay only shuttles candidate addresses and a session nonce. It sees no
/// channel content, and neither peer's Sigil: both connect with a throwaway identity, the requester names the
/// target only by a handle, and the target proves it owns that handle with a key made for this relay alone.
///
/// A target D <c>Reserve</c>s under a <see cref="Handle"/> — the hash of its rendezvous key for this relay — and
/// signs the reservation with that key over this connection's handshake hash, so the reservation cannot be squatted
/// and cannot be replayed on any other connection. A requester E, who got the handle from D's link,
/// <c>Rendezvous</c>es for it; the relay pushes a <c>Notify</c> to D and replies with an <c>Offer</c> to E, each
/// carrying the other's punch candidates and a shared nonce.
///
/// Every message is <c>type ‖ version ‖ …</c>, the version being the CupriMark <c>ferryman</c> ordinal.
/// </summary>
internal static class FerrymanProtocol
{
    public const byte MsgReserve = 1;    // D -> relay : { handle, candidates, rendezvous key, signature }
    public const byte MsgReserved = 2;   // relay -> D : { status }
    public const byte MsgRendezvous = 3; // E -> relay : { handle, candidates }
    public const byte MsgOffer = 4;      // relay -> E : { status, nonce, candidates(of D) }
    public const byte MsgNotify = 5;     // relay -> D : { nonce, candidates(of E) } (pushed)
    public const byte MsgKeepAlive = 6;  // D -> relay : { } — the reservation is still wanted

    public const byte StatusOk = 0;
    public const byte StatusNotReserved = 1;

    public const int HandleSize = RelayRoute.HandleSize;
    public const int NonceSize = 16;
    public const int MaxCandidates = 8;

    private static byte Version => checked((byte)CupriMarks.Supported(CupriMarks.Ferryman).Max);

    /// <summary>
    /// The handle a target is reserved under: a hash of its rendezvous public key. The relay keys reservations by
    /// this. It is not derived from the target's Sigil, so knowing a Sigil does not reveal it.
    /// </summary>
    public static byte[] Handle(ReadOnlySpan<byte> rendezvousPublicKey)
    {
        var buf = new byte[DomainHandle.Length + rendezvousPublicKey.Length];
        DomainHandle.CopyTo(buf, 0);
        rendezvousPublicKey.CopyTo(buf.AsSpan(DomainHandle.Length));
        return SHA256.HashData(buf)[..HandleSize];
    }

    private static readonly byte[] DomainHandle = "cuprinet/ferryman/handle/v2"u8.ToArray();

    /// <summary>
    /// The exact bytes a target signs to reserve <paramref name="handle"/>: a label, the handshake hash of its
    /// connection to the relay, the handle and its candidates. The handshake hash is what makes the reservation
    /// good for this one connection and no other.
    /// </summary>
    public static byte[] ReserveSubject(ReadOnlySpan<byte> handshakeHash, byte[] handle, IReadOnlyList<IPEndPoint> candidates)
    {
        var w = new CodexWriter();
        w.WriteBytes(DomainReserveSig);
        w.WriteBytes(handshakeHash);
        w.WriteBytes(handle);
        WriteCandidates(w, candidates);
        return w.ToArray();
    }

    private static readonly byte[] DomainReserveSig = "cuprinet/ferryman/reserve/v2"u8.ToArray();

    public static byte[] Reserve(byte[] handle, IReadOnlyList<IPEndPoint> candidates, byte[] rendezvousPublicKey, byte[] signature)
    {
        var w = Begin(MsgReserve);
        w.WriteBytes(handle);
        WriteCandidates(w, candidates);
        w.WriteBytes(rendezvousPublicKey);
        w.WriteBytes(signature);
        return w.ToArray();
    }

    public static byte[] Reserved(byte status)
    {
        var w = Begin(MsgReserved);
        w.WriteByte(status);
        return w.ToArray();
    }

    public static byte[] Rendezvous(byte[] handle, IReadOnlyList<IPEndPoint> candidates)
    {
        var w = Begin(MsgRendezvous);
        w.WriteBytes(handle);
        WriteCandidates(w, candidates);
        return w.ToArray();
    }

    public static byte[] Offer(byte status, byte[] nonce, IReadOnlyList<IPEndPoint> targetCandidates)
    {
        var w = Begin(MsgOffer);
        w.WriteByte(status);
        w.WriteBytes(nonce);
        WriteCandidates(w, targetCandidates);
        return w.ToArray();
    }

    public static byte[] Notify(byte[] nonce, IReadOnlyList<IPEndPoint> requesterCandidates)
    {
        var w = Begin(MsgNotify);
        w.WriteBytes(nonce);
        WriteCandidates(w, requesterCandidates);
        return w.ToArray();
    }

    public static byte[] KeepAlive() => Begin(MsgKeepAlive).ToArray();

    /// <summary>
    /// The candidates out of <paramref name="candidates"/> a punch could ever reach: no unspecified, broadcast or
    /// multicast address, no port 0, IPv4-mapped addresses unwrapped, duplicates dropped. Loopback and private
    /// addresses stay — they are how two nodes on one host, or one LAN, find each other through a relay.
    /// </summary>
    public static List<IPEndPoint> Usable(IEnumerable<IPEndPoint> candidates)
    {
        var usable = new List<IPEndPoint>();
        foreach (var candidate in candidates)
        {
            var address = candidate.Address.IsIPv4MappedToIPv6 ? candidate.Address.MapToIPv4() : candidate.Address;
            if (candidate.Port is <= 0 or > 65535
                || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)
                || address.IsIPv6Multicast
                || (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && address.GetAddressBytes()[0] is >= 224 and <= 239))
                continue;
            var endpoint = new IPEndPoint(address, candidate.Port);
            if (!usable.Contains(endpoint))
                usable.Add(endpoint);
        }
        return usable;
    }

    private static CodexWriter Begin(byte type)
    {
        var w = new CodexWriter();
        w.WriteByte(type);
        w.WriteByte(Version);
        return w;
    }

    // ---- parsing ------------------------------------------------------------------------------

    /// <summary>The message type of a payload, or 0 if it is empty.</summary>
    public static byte TypeOf(ReadOnlySpan<byte> payload) => payload.Length > 0 ? payload[0] : (byte)0;

    public static bool TryReadReserve(ReadOnlySpan<byte> payload, out byte[] handle, out List<IPEndPoint> candidates, out byte[] rendezvousPublicKey, out byte[] signature)
    {
        handle = [];
        candidates = [];
        rendezvousPublicKey = [];
        signature = [];
        try
        {
            var r = new CodexReader(payload);
            if (!ReadHeader(ref r, MsgReserve))
                return false;
            handle = r.ReadBytes().ToArray();
            if (handle.Length != HandleSize)
                return false;
            candidates = ReadCandidates(ref r);
            rendezvousPublicKey = r.ReadBytes().ToArray();
            signature = r.ReadBytes().ToArray();
            return rendezvousPublicKey.Length is > 0 and <= 64 && signature.Length is > 0 and <= 128;
        }
        catch (CodexFormatException) { return false; }
    }

    public static bool TryReadReserved(ReadOnlySpan<byte> payload, out byte status)
    {
        status = StatusNotReserved;
        try
        {
            var r = new CodexReader(payload);
            if (!ReadHeader(ref r, MsgReserved))
                return false;
            status = r.ReadByte();
            return true;
        }
        catch (CodexFormatException) { return false; }
    }

    public static bool TryReadRendezvous(ReadOnlySpan<byte> payload, out byte[] handle, out List<IPEndPoint> candidates)
    {
        handle = [];
        candidates = [];
        try
        {
            var r = new CodexReader(payload);
            if (!ReadHeader(ref r, MsgRendezvous))
                return false;
            handle = r.ReadBytes().ToArray();
            if (handle.Length != HandleSize)
                return false;
            candidates = ReadCandidates(ref r);
            return true;
        }
        catch (CodexFormatException) { return false; }
    }

    public static bool TryReadOffer(ReadOnlySpan<byte> payload, out byte status, out byte[] nonce, out List<IPEndPoint> candidates)
    {
        status = StatusNotReserved;
        nonce = [];
        candidates = [];
        try
        {
            var r = new CodexReader(payload);
            if (!ReadHeader(ref r, MsgOffer))
                return false;
            status = r.ReadByte();
            nonce = r.ReadBytes().ToArray();
            if (nonce.Length != NonceSize)
                return false;
            candidates = ReadCandidates(ref r);
            return true;
        }
        catch (CodexFormatException) { return false; }
    }

    public static bool TryReadNotify(ReadOnlySpan<byte> payload, out byte[] nonce, out List<IPEndPoint> candidates)
    {
        nonce = [];
        candidates = [];
        try
        {
            var r = new CodexReader(payload);
            if (!ReadHeader(ref r, MsgNotify))
                return false;
            nonce = r.ReadBytes().ToArray();
            if (nonce.Length != NonceSize)
                return false;
            candidates = ReadCandidates(ref r);
            return true;
        }
        catch (CodexFormatException) { return false; }
    }

    private static bool ReadHeader(ref CodexReader r, byte expected)
        => r.ReadByte() == expected && CupriMarks.Accepts(CupriMarks.Ferryman, r.ReadByte());

    private static void WriteCandidates(CodexWriter w, IReadOnlyList<IPEndPoint> candidates)
    {
        var count = Math.Min(candidates.Count, MaxCandidates);
        w.WriteVarUInt((ulong)count);
        for (var i = 0; i < count; i++)
        {
            w.WriteBytes(candidates[i].Address.GetAddressBytes());
            w.WriteVarUInt((ulong)candidates[i].Port);
        }
    }

    private static List<IPEndPoint> ReadCandidates(ref CodexReader r)
    {
        var count = r.ReadVarUInt();
        if (count > MaxCandidates)
            throw new CodexFormatException("Too many Ferryman candidates.");
        var list = new List<IPEndPoint>((int)count);
        for (var i = 0UL; i < count; i++)
        {
            var addr = r.ReadBytes().ToArray();
            var port = r.ReadVarUInt();
            if ((addr.Length != 4 && addr.Length != 16) || port is 0 or > 65535)
                throw new CodexFormatException("Malformed Ferryman candidate.");
            list.Add(new IPEndPoint(new IPAddress(addr), (int)port));
        }
        return list;
    }
}
