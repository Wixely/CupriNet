using System.Buffers.Binary;

namespace CupriNet.Vessel;

/// <summary>
/// A minimal, bounded reliable-ordered ARQ over an unreliable datagram channel (the "KCP route": a pure-managed
/// reliable-UDP layer so a Noise/mux session can run over a hole-punched UDP path, with no native dependency).
/// It is a cumulative-ack sliding-window protocol (a simplified TCP): segments are sequenced, every datagram
/// piggybacks the sender's next-expected sequence as a cumulative ack, unacknowledged segments are retransmitted
/// on a timeout, and delivery to the application is strictly in order.
/// <para>
/// The class is pure and clock-injected (all timing is via the <c>nowMs</c> argument) with output via a callback,
/// so it can be driven deterministically over an in-memory lossy channel in tests, and over a real UDP socket in
/// production. Every buffer is bounded (send/receive windows) to meet the hostile-network bar — a peer cannot
/// force unbounded allocation. Congestion control is intentionally omitted for v1 (a fixed window); it can be
/// added without changing the wire format.
/// </para>
/// </summary>
public sealed class ReliableArq
{
    // Wire: [cmd:1][seq:u32][una:u32][len:u16][payload:len]. una is this sender's rcvNext (cumulative ack).
    private const byte CmdPush = 1;  // carries payload at `seq`
    private const byte CmdAck = 2;   // carries only the cumulative `una`
    private const byte CmdClose = 3; // a sequenced end-of-stream marker (delivered in order, like an empty push)
    private const int Header = 11;

    /// <summary>Smallest valid segment (header, no payload) — a cheap pre-filter for a listener before it allocates state.</summary>
    internal const int MinSegmentSize = Header;

    /// <summary>Max application payload per segment (keeps a datagram comfortably under a typical MTU).</summary>
    public const int Mss = 1152;

    private readonly int _sendWindow;
    private readonly int _recvWindow;
    private readonly int _rtoMinMs;
    private readonly int _rtoMaxMs;
    private readonly Action<ReadOnlyMemory<byte>> _output;

    private readonly Queue<byte[]> _sendQueue = new();   // app chunks not yet assigned a sequence
    private readonly LinkedList<Segment> _sendBuf = new(); // in-flight, unacknowledged (seq in [_sndUna, _sndNext))
    private readonly Dictionary<uint, byte[]?> _recvBuf = new(); // out-of-order, buffered until the gap fills (null = CLOSE)
    private readonly Queue<byte[]> _deliverQueue = new(); // in-order payloads ready for the application

    private uint _sndNext;
    private uint _sndUna;
    private uint _rcvNext;
    private bool _needAck;
    private bool _localClosed;   // we've queued our CLOSE
    private bool _closeSent;     // our CLOSE has been assigned a sequence
    private bool _peerClosed;    // peer's CLOSE has been delivered in order → EOF for the reader

    private sealed class Segment
    {
        public uint Seq;
        public byte Cmd;
        public byte[] Payload = [];
        public long ResendAtMs;
        public int RtoMs;
    }

    public ReliableArq(Action<ReadOnlyMemory<byte>> output, int sendWindow = 128, int recvWindow = 256, int rtoMinMs = 100, int rtoMaxMs = 4000)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _sendWindow = Math.Max(1, sendWindow);
        _recvWindow = Math.Max(1, recvWindow);
        _rtoMinMs = Math.Max(10, rtoMinMs);
        _rtoMaxMs = Math.Max(_rtoMinMs, rtoMaxMs);
    }

    /// <summary>True once the peer's end-of-stream has been delivered in order and all data drained.</summary>
    public bool PeerFinished => _peerClosed && _deliverQueue.Count == 0;

    /// <summary>Segments still awaiting acknowledgement (for tests/telemetry).</summary>
    public int InFlight => _sendBuf.Count;

    /// <summary>Queues application data for reliable, ordered delivery. Split into MSS-sized segments.</summary>
    public void Send(ReadOnlySpan<byte> data)
    {
        if (_localClosed)
            throw new InvalidOperationException("Cannot send after Close.");
        while (!data.IsEmpty)
        {
            var take = Math.Min(Mss, data.Length);
            _sendQueue.Enqueue(data[..take].ToArray());
            data = data[take..];
        }
    }

    /// <summary>Queues a sequenced end-of-stream marker; no further <see cref="Send"/> is allowed.</summary>
    public void Close() => _localClosed = true;

    /// <summary>Pulls the next in-order application payload, or null if none is ready yet.</summary>
    public byte[]? Receive() => _deliverQueue.Count > 0 ? _deliverQueue.Dequeue() : null;

    /// <summary>How many segments may still be queued before back-pressure should stop the writer.</summary>
    public bool CanQueueMore => _sendQueue.Count + _sendBuf.Count < _sendWindow * 2;

    /// <summary>Feeds one received datagram into the protocol (ignoring anything malformed or out of window).</summary>
    public void Input(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < Header)
            return;
        var cmd = datagram[0];
        if (cmd is not (CmdPush or CmdAck or CmdClose))
            return; // a foreign datagram (e.g. a hole-punch probe sharing the socket) — ignore it entirely
        var seq = BinaryPrimitives.ReadUInt32BigEndian(datagram[1..]);
        var una = BinaryPrimitives.ReadUInt32BigEndian(datagram[5..]);
        var len = BinaryPrimitives.ReadUInt16BigEndian(datagram[9..]);
        if (datagram.Length < Header + len)
            return;

        AcknowledgeThrough(una); // cumulative ack: everything below `una` is confirmed received by the peer

        switch (cmd)
        {
            case CmdPush:
                AcceptSequenced(seq, datagram.Slice(Header, len).ToArray());
                break;
            case CmdClose:
                AcceptSequenced(seq, null); // null payload marks the ordered end-of-stream
                break;
            case CmdAck:
                break; // pure ack — the `una` handling above was the whole point
        }
    }

    private void AcknowledgeThrough(uint una)
    {
        if (SeqLess(_sndUna, una))
            _sndUna = una;
        while (_sendBuf.First is { } node && SeqLess(node.Value.Seq, _sndUna))
            _sendBuf.RemoveFirst();
    }

    private void AcceptSequenced(uint seq, byte[]? payload)
    {
        // Already delivered, or beyond the receive window → drop, but (re-)ack so a lost ack recovers.
        if (SeqLess(seq, _rcvNext) || SeqGreaterEqual(seq, _rcvNext + (uint)_recvWindow))
        {
            _needAck = true;
            return;
        }
        _recvBuf.TryAdd(seq, payload);

        // Advance in-order: move contiguous segments from the reorder buffer to the delivery queue.
        while (_recvBuf.TryGetValue(_rcvNext, out var next))
        {
            _recvBuf.Remove(_rcvNext);
            if (next is null)
                _peerClosed = true;      // the CLOSE marker — everything before it has now been delivered
            else if (next.Length > 0)
                _deliverQueue.Enqueue(next);
            _rcvNext++;
            if (_peerClosed)
                break;
        }
        _needAck = true;
    }

    /// <summary>
    /// Drives timers: assigns sequences to queued data within the window, (re)transmits due segments, and flushes
    /// a pending ack. Returns the earliest time (ms) it next needs to run, so a driver can sleep until then.
    /// </summary>
    public long Update(long nowMs)
    {
        // Promote queued app data (and our CLOSE) into the send window.
        while (SeqLess(_sndNext, _sndUna + (uint)_sendWindow))
        {
            byte[] payload;
            byte cmd;
            if (_sendQueue.Count > 0)
            {
                payload = _sendQueue.Dequeue();
                cmd = CmdPush;
            }
            else if (_localClosed && !_closeSent)
            {
                payload = [];
                cmd = CmdClose;
                _closeSent = true;
            }
            else
            {
                break;
            }

            var seg = new Segment { Seq = _sndNext, Cmd = cmd, Payload = payload, RtoMs = _rtoMinMs, ResendAtMs = nowMs };
            _sendBuf.AddLast(seg);
            _sndNext++;
        }

        var nextEvent = nowMs + 1000;
        foreach (var seg in _sendBuf)
        {
            if (nowMs >= seg.ResendAtMs)
            {
                Transmit(seg.Cmd, seg.Seq, seg.Payload);
                seg.RtoMs = Math.Min(seg.RtoMs * 2, _rtoMaxMs); // exponential backoff on loss
                seg.ResendAtMs = nowMs + seg.RtoMs;
            }
            nextEvent = Math.Min(nextEvent, seg.ResendAtMs);
        }

        if (_needAck)
        {
            Transmit(CmdAck, 0, []);
            _needAck = false;
        }
        return nextEvent;
    }

    private void Transmit(byte cmd, uint seq, ReadOnlySpan<byte> payload)
    {
        var datagram = new byte[Header + payload.Length];
        datagram[0] = cmd;
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(1), seq);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(5), _rcvNext); // piggyback our cumulative ack
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(9), (ushort)payload.Length);
        payload.CopyTo(datagram.AsSpan(Header));
        _output(datagram);
    }

    // Serial-number comparison (RFC 1982 style) so the 32-bit sequence space wraps correctly.
    private static bool SeqLess(uint a, uint b) => (int)(a - b) < 0;
    private static bool SeqGreaterEqual(uint a, uint b) => (int)(a - b) >= 0;
}
