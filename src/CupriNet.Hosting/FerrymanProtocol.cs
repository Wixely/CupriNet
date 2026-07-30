using System.Net;
using System.Security.Cryptography;
using CupriNet.Abstractions;
using CupriNet.Codex;

namespace CupriNet.Hosting;

/// <summary>
/// Wire format for the Ferryman rendezvous — the small signaling exchange a relay brokers so two NAT'd peers can
/// hole-punch a direct connection. The relay only shuttles candidate addresses and a session nonce; it never sees
/// the peers' real identities (both connect with an ephemeral key) nor any channel content.
///
/// A target D <c>Reserve</c>s under a <see cref="Handle"/> derived from its Sigil (so the relay learns a blinded
/// handle, not D's identity). A requester E <c>Rendezvous</c>es for that handle; the relay pushes a <c>Notify</c>
/// to D and replies with an <c>Offer</c> to E, each carrying the other's punch candidates and a shared nonce.
/// </summary>
internal static class FerrymanProtocol
{
    public const byte MsgReserve = 1;    // D -> relay : { handle, candidates }
    public const byte MsgReserved = 2;   // relay -> D : { status }
    public const byte MsgRendezvous = 3; // E -> relay : { handle, candidates }
    public const byte MsgOffer = 4;      // relay -> E : { status, nonce, candidates(of D) }
    public const byte MsgNotify = 5;     // relay -> D : { nonce, candidates(of E) } (pushed)

    public const byte StatusOk = 0;
    public const byte StatusNotReserved = 1;

    public const int HandleSize = 16;
    public const int NonceSize = 16;
    public const int MaxCandidates = 8;

    /// <summary>A relay-facing, blinded handle for a target: the relay keys reservations by this, not by the Sigil.</summary>
    public static byte[] Handle(Sigil target)
    {
        var buf = new byte[DomainReserve.Length + Sigil.Size];
        DomainReserve.CopyTo(buf, 0);
        target.Span.CopyTo(buf.AsSpan(DomainReserve.Length));
        return SHA256.HashData(buf)[..HandleSize];
    }

    private static readonly byte[] DomainReserve = "cuprinet/ferryman/handle/v1"u8.ToArray();

    public static byte[] Reserve(byte[] handle, IReadOnlyList<IPEndPoint> candidates)
    {
        var w = new CodexWriter();
        w.WriteByte(MsgReserve);
        w.WriteBytes(handle);
        WriteCandidates(w, candidates);
        return w.ToArray();
    }

    public static byte[] Reserved(byte status) => [MsgReserved, status];

    public static byte[] Rendezvous(byte[] handle, IReadOnlyList<IPEndPoint> candidates)
    {
        var w = new CodexWriter();
        w.WriteByte(MsgRendezvous);
        w.WriteBytes(handle);
        WriteCandidates(w, candidates);
        return w.ToArray();
    }

    public static byte[] Offer(byte status, byte[] nonce, IReadOnlyList<IPEndPoint> candidates)
    {
        var w = new CodexWriter();
        w.WriteByte(MsgOffer);
        w.WriteByte(status);
        w.WriteBytes(nonce);
        WriteCandidates(w, candidates);
        return w.ToArray();
    }

    public static byte[] Notify(byte[] nonce, IReadOnlyList<IPEndPoint> candidates)
    {
        var w = new CodexWriter();
        w.WriteByte(MsgNotify);
        w.WriteBytes(nonce);
        WriteCandidates(w, candidates);
        return w.ToArray();
    }

    // ---- parsing ------------------------------------------------------------------------------

    public static bool TryReadReserve(ReadOnlySpan<byte> payload, out byte[] handle, out List<IPEndPoint> candidates)
        => TryReadHandleAndCandidates(payload, MsgReserve, out handle, out candidates);

    public static bool TryReadRendezvous(ReadOnlySpan<byte> payload, out byte[] handle, out List<IPEndPoint> candidates)
        => TryReadHandleAndCandidates(payload, MsgRendezvous, out handle, out candidates);

    public static bool TryReadOffer(ReadOnlySpan<byte> payload, out byte status, out byte[] nonce, out List<IPEndPoint> candidates)
    {
        status = StatusNotReserved;
        nonce = [];
        candidates = [];
        try
        {
            var r = new CodexReader(payload);
            if (r.ReadByte() != MsgOffer)
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
            if (r.ReadByte() != MsgNotify)
                return false;
            nonce = r.ReadBytes().ToArray();
            if (nonce.Length != NonceSize)
                return false;
            candidates = ReadCandidates(ref r);
            return true;
        }
        catch (CodexFormatException) { return false; }
    }

    private static bool TryReadHandleAndCandidates(ReadOnlySpan<byte> payload, byte expected, out byte[] handle, out List<IPEndPoint> candidates)
    {
        handle = [];
        candidates = [];
        try
        {
            var r = new CodexReader(payload);
            if (r.ReadByte() != expected)
                return false;
            handle = r.ReadBytes().ToArray();
            if (handle.Length != HandleSize)
                return false;
            candidates = ReadCandidates(ref r);
            return true;
        }
        catch (CodexFormatException) { return false; }
    }

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
