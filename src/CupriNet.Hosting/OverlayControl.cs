using CupriNet.Alembic;
using CupriNet.Arcanum;
using CupriNet.Codex;
using CupriNet.Concordance;
using CupriNet.Vessel;

namespace CupriNet.Hosting;

/// <summary>
/// The L1 overlay control plane: request/response messages a node exchanges over a dedicated, Noise-encrypted
/// connection to find peers near a routing coordinate (DIVINE→peer records), publish a channel advertisement
/// (PUBLISH→ack), and look up advertisements by Glyph window (LOOKUP→decrees). These are the on-wire form of
/// the delegates that <see cref="Divination"/> and <see cref="ChannelDirectory"/> take; here they run over real
/// connections instead of the test harness's in-memory callbacks.
/// </summary>
internal static class OverlayControl
{
    /// <summary>The stream carrying the session-kind marker and all control request/response frames.</summary>
    public const ushort Stream = 6;

    /// <summary>Session-kind markers sent by the initiator right after the Noise handshake.</summary>
    public const byte KindChannel = 1;
    public const byte KindControl = 2;

    /// <summary>
    /// A decoy channel session (an "Effigy"): on the wire — encrypted frames on the channel stream — it is
    /// indistinguishable from <see cref="KindChannel"/>; the marker only tells the two honest endpoints to run
    /// cover traffic instead of a real Consecration. It is never surfaced to the app, published, or persisted.
    /// </summary>
    public const byte KindEffigy = 3;

    /// <summary>The stream Effigy cover traffic rides — the same as the Epistle (chat) rite, so it mimics chat.</summary>
    public const ushort EffigyStream = 3;

    /// <summary>
    /// A Pageant (fake-group) clique edge. Like <see cref="KindEffigy"/> it is a decoy channel session on the
    /// wire; the initiator sends the Pageant id as its first frame so the far side can bind the edge to the group.
    /// </summary>
    public const byte KindPageant = 4;

    /// <summary>A Ferryman rendezvous session: the initiator reserves or requests a brokered hole punch (signaling only).</summary>
    public const byte KindFerryman = 5;

    public const byte OpDivine = 1;
    public const byte OpPublish = 2;
    public const byte OpLookup = 3;
    public const byte OpSample = 4;
    public const byte OpPing = 5;
    public const byte OpPageant = 6;

    public const byte StatusOk = 0;
    public const byte StatusRejected = 1;

    public const int MaxAugury = 16;
    public const int MaxLookupGlyphs = 8;
    public const int MaxLookupDecrees = 16;

    /// <summary>Cap on the padding a PING may carry or request, so cover traffic can't be turned into an amplifier.</summary>
    public const int MaxPingPad = 4096;

    // ---- Requests (client -> server) -----------------------------------------------------------

    public static byte[] DivineRequest(RoutingKey target)
    {
        var w = new CodexWriter();
        w.WriteByte(OpDivine);
        w.WriteBytes(target.Span);
        return w.ToArray();
    }

    public static byte[] PublishRequest(byte[] decreeBytes, byte[] tributeNonce)
    {
        var w = new CodexWriter();
        w.WriteByte(OpPublish);
        w.WriteBytes(decreeBytes);   // the exact bytes are also the Tribute subject
        w.WriteBytes(tributeNonce);
        return w.ToArray();
    }

    public static byte[] SampleRequest(int count)
    {
        var w = new CodexWriter();
        w.WriteByte(OpSample);
        w.WriteVarUInt((ulong)count);
        return w.ToArray();
    }

    /// <summary>
    /// A hot-fuzz heartbeat: carries <paramref name="upPad"/> random bytes and asks the responder to pad its
    /// reply to <paramref name="downPad"/> bytes. Keeps a decoy connection warm and shapes both directions so a
    /// decoy link resembles a bidirectional chat rather than a thin request/response.
    /// </summary>
    public static byte[] PingRequest(int downPad, ReadOnlySpan<byte> upPad)
    {
        var w = new CodexWriter();
        w.WriteByte(OpPing);
        w.WriteVarUInt((ulong)Math.Clamp(downPad, 0, MaxPingPad));
        w.WriteBytes(upPad);
        return w.ToArray();
    }

    /// <summary>
    /// The reply to a PING: an OK status byte followed by <paramref name="downPad"/> padding bytes. Built raw
    /// (the client never parses the padding) so the reply hits the requested size exactly; it is opaque on the
    /// wire once encrypted.
    /// </summary>
    public static byte[] PingResponse(int downPad)
    {
        var buf = new byte[1 + Math.Clamp(downPad, 0, MaxPingPad)];
        buf[0] = StatusOk;
        return buf;
    }

    /// <summary>An invitation to join a Pageant (fake group): the initiator sends the whole group definition.</summary>
    public static byte[] PageantInviteRequest(Pageant pageant)
    {
        var w = new CodexWriter();
        w.WriteByte(OpPageant);
        w.WriteBytes(PageantCodec.Encode(pageant));
        return w.ToArray();
    }

    public static byte[] LookupRequest(IReadOnlyList<byte[]> glyphWindow)
    {
        var w = new CodexWriter();
        w.WriteByte(OpLookup);
        w.WriteVarUInt((ulong)glyphWindow.Count);
        foreach (var glyph in glyphWindow)
            w.WriteBytes(glyph);
        return w.ToArray();
    }

    // ---- Responses (server -> client) ----------------------------------------------------------

    public static byte[] PeerRecordsResponse(IReadOnlyList<PeerRecord> records)
    {
        var count = Math.Min(records.Count, MaxAugury);
        var w = new CodexWriter();
        w.WriteVarUInt((ulong)count);
        for (var i = 0; i < count; i++)
            w.WriteBytes(PeerRecordCodec.Encode(records[i]));
        return w.ToArray();
    }

    public static byte[] DecreesResponse(IReadOnlyList<Decree> decrees)
    {
        var count = Math.Min(decrees.Count, MaxLookupDecrees);
        var w = new CodexWriter();
        w.WriteVarUInt((ulong)count);
        for (var i = 0; i < count; i++)
            w.WriteBytes(DecreeCodec.Encode(decrees[i]));
        return w.ToArray();
    }

    public static byte[] StatusResponse(byte status) => [status];

    // ---- Client-side parsing (only accepts well-formed, signature-verified records) -------------

    public static IReadOnlyList<PeerRecord> ParsePeerRecords(byte[] payload, ICryptoSuite suite)
    {
        var reader = new CodexReader(payload);
        ulong count;
        try { count = reader.ReadVarUInt(); }
        catch { return []; }
        if (count > (ulong)MaxAugury)
            return [];

        var records = new List<PeerRecord>((int)count);
        for (var i = 0UL; i < count; i++)
        {
            try
            {
                var record = PeerRecordCodec.Decode(reader.ReadBytes()).Record;
                if (PeerRecordSigner.Verify(record, suite))
                    records.Add(record);
            }
            catch { break; }
        }
        return records;
    }

    public static IReadOnlyList<Decree> ParseDecrees(byte[] payload)
    {
        var reader = new CodexReader(payload);
        ulong count;
        try { count = reader.ReadVarUInt(); }
        catch { return []; }
        if (count > (ulong)MaxLookupDecrees)
            return [];

        var decrees = new List<Decree>((int)count);
        for (var i = 0UL; i < count; i++)
        {
            try { decrees.Add(DecreeCodec.Decode(reader.ReadBytes()).Decree); }
            catch { break; }
        }
        return decrees;
    }
}

/// <summary>
/// A per-connection sliding-window rate limiter for overlay-control requests. Cheap and monotonic (fed a
/// millisecond clock), it caps how fast one peer can drive a served connection; exceeding the cap drops it.
/// </summary>
internal sealed class ControlRateLimiter(int maxPerWindow, long windowMs)
{
    private long _windowStart;
    private int _count;

    /// <summary>Records a request at <paramref name="nowMs"/> and returns false once the window's cap is exceeded.</summary>
    public bool Allow(long nowMs)
    {
        if (nowMs - _windowStart > windowMs)
        {
            _windowStart = nowMs;
            _count = 0;
        }
        _count++;
        return _count <= maxPerWindow;
    }
}

/// <summary>
/// A per-peer overlay-control budget shared across all of one peer's connections (keyed by its overlay
/// Sigil): a request rate limit plus a concurrent-connection cap, so a peer cannot escape the rate limit by
/// opening many connections. Thread-safe — several of a peer's connections may hit it at once.
/// </summary>
internal sealed class PeerControlBudget(int maxRequestsPerWindow, long windowMs)
{
    private readonly object _gate = new();
    private readonly ControlRateLimiter _limiter = new(maxRequestsPerWindow, windowMs);
    private int _connections;

    /// <summary>Reserves a connection slot for this peer, or returns false if it is already at its cap.</summary>
    public bool TryOpenConnection(int maxConnections)
    {
        lock (_gate)
        {
            if (_connections >= maxConnections)
                return false;
            _connections++;
            return true;
        }
    }

    /// <summary>Releases a connection slot; returns the peer's remaining connection count.</summary>
    public int CloseConnection()
    {
        lock (_gate)
            return _connections > 0 ? --_connections : 0;
    }

    /// <summary>Records a request against the peer's shared rate limit; false once the window's cap is exceeded.</summary>
    public bool Allow(long nowMs)
    {
        lock (_gate)
            return _limiter.Allow(nowMs);
    }
}

/// <summary>A pooled, Noise-encrypted control connection to one overlay peer; requests are serialized over it.</summary>
internal sealed class ControlConnection(IVessel vessel) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IVessel Vessel { get; } = vessel;

    /// <summary>Sends one request and reads its response (serialized against other callers on this connection).</summary>
    public async Task<byte[]> RoundtripAsync(byte[] request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Vessel.SendAsync(OverlayControl.Stream, request, cancellationToken).ConfigureAwait(false);
            var frame = await Vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new IOException("Control connection closed.");
            if (frame.StreamId != OverlayControl.Stream)
                throw new IOException($"Unexpected frame on stream {frame.StreamId} during control exchange.");
            return frame.Payload;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await Vessel.DisposeAsync().ConfigureAwait(false);
    }
}
