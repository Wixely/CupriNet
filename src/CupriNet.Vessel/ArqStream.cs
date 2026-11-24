using System.Diagnostics;
using System.Net;

namespace CupriNet.Vessel;

/// <summary>
/// A reliable, ordered byte <see cref="Stream"/> over an unreliable <see cref="IPacketLink"/> (UDP), backed by
/// <see cref="ReliableArq"/>. This is the adapter that lets the rest of the stack — the framing <see cref="Vessel"/>,
/// the Noise handshake, and the mux — run over a hole-punched UDP path exactly as they do over TCP, with no native
/// dependency. The single (non-thread-safe) ARQ instance is serialized behind one gate; background loops pump the
/// link, drive the retransmit/ack timer, and flush outbound datagrams.
/// </summary>
public sealed class ArqStream : Stream
{
    private readonly IPacketLink _link;
    private readonly ReliableArq _arq;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _outbound = new();
    private readonly Queue<byte[]> _delivered = new();
    private readonly SemaphoreSlim _readReady = new(0, int.MaxValue);
    private readonly SemaphoreSlim _writeReady = new(0, int.MaxValue);
    private readonly SemaphoreSlim _pumpWake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _sendWake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveLoop;
    private readonly Task _pumpLoop;
    private readonly Task _sendLoop;

    private byte[] _current = [];
    private int _currentPos;
    private bool _eof;
    private bool _dead;

    /// <summary>How long a close waits for the peer to acknowledge the end-of-stream marker.</summary>
    private static readonly TimeSpan CloseLinger = TimeSpan.FromSeconds(1);

    /// <param name="link">The datagram path; the stream owns and disposes it.</param>
    /// <param name="connectionId">The id both ends agreed on (see <see cref="UdpVessel"/>), or null to adopt the
    /// one on the peer's first segment — the accepting side of a listener.</param>
    public ArqStream(IPacketLink link, ulong? connectionId)
    {
        ArgumentNullException.ThrowIfNull(link);
        _link = link;
        _arq = new ReliableArq(OnOutput, connectionId: connectionId);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _pumpLoop = Task.Run(PumpLoopAsync);
        _sendLoop = Task.Run(SendLoopAsync);
    }

    public EndPoint? LocalEndPoint => _link.LocalEndPoint;
    public EndPoint? RemoteEndPoint => _link.RemoteEndPoint;

    // Called under _gate from the ARQ (Update/Input): queue the datagram; the send loop flushes it to the link.
    private void OnOutput(ReadOnlyMemory<byte> datagram)
    {
        _outbound.Enqueue(datagram.ToArray());
        Release(_sendWake);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var datagram = await _link.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (datagram is null)
                    break; // link closed
                lock (_gate)
                {
                    _arq.Input(datagram);
                    DrainLocked();
                }
                Release(_pumpWake);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            lock (_gate)
            {
                _eof = true;
                Release(_readReady);
            }
        }
    }

    private async Task PumpLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                long next;
                lock (_gate)
                {
                    next = _arq.Update(Environment.TickCount64);
                    DrainLocked();
                    if (_arq.Dead && !_dead)
                    {
                        // The peer stopped answering: readers see end-of-stream, writers an error. Nothing is
                        // left waiting on a connection that will never move again.
                        _dead = true;
                        _eof = true;
                        Release(_readReady);
                    }
                }
                Release(_writeReady); // acks may have freed send-window space
                var delay = (int)Math.Clamp(next - Environment.TickCount64, 1, 1000);
                try { await _pumpWake.WaitAsync(delay, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch { }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _sendWake.WaitAsync(_cts.Token).ConfigureAwait(false);
                byte[][] batch;
                lock (_gate)
                {
                    batch = _outbound.ToArray();
                    _outbound.Clear();
                }
                foreach (var datagram in batch)
                    await _link.SendAsync(datagram, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void DrainLocked()
    {
        var delivered = false;
        while (_arq.Receive() is { } chunk)
        {
            _delivered.Enqueue(chunk);
            delivered = true;
        }
        if (_arq.PeerFinished)
        {
            _eof = true;
            delivered = true;
        }
        if (delivered)
            Release(_readReady);
    }

    private static void Release(SemaphoreSlim signal)
    {
        if (signal.CurrentCount == 0)
        {
            try { signal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_currentPos >= _current.Length && _delivered.Count > 0)
                {
                    _current = _delivered.Dequeue();
                    _currentPos = 0;
                }
                if (_currentPos < _current.Length)
                {
                    var n = Math.Min(buffer.Length, _current.Length - _currentPos);
                    _current.AsSpan(_currentPos, n).CopyTo(buffer.Span);
                    _currentPos += n;
                    return n;
                }
                if (_eof)
                    return 0;
            }
            await _readReady.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            bool queued;
            lock (_gate)
            {
                if (_dead)
                    throw new IOException("The reliable-UDP peer stopped responding.");
                queued = _arq.CanQueueMore;
                if (queued)
                    _arq.Send(buffer.Span);
            }
            if (queued)
                break;
            await _writeReady.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        Release(_pumpWake);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            lock (_gate)
            {
                _arq.Close();
                Release(_pumpWake);
            }
            // Wait for the peer to acknowledge the CLOSE marker, so that it sees a clean end-of-stream even when
            // the first copy is lost — the pump retransmits it meanwhile. A peer that has already closed its own
            // side is probably gone, so it gets only a moment; a dead one gets none.
            var waited = Stopwatch.StartNew();
            while (true)
            {
                bool done;
                TimeSpan budget;
                lock (_gate)
                {
                    done = _arq.CloseAcknowledged || _arq.Dead;
                    budget = _arq.PeerClosed ? TimeSpan.FromMilliseconds(80) : CloseLinger;
                }
                if (done || waited.Elapsed >= budget)
                    break;
                await Task.Delay(10).ConfigureAwait(false);
            }
        }
        catch { }

        await _cts.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(_receiveLoop, _pumpLoop, _sendLoop).ConfigureAwait(false); } catch { }
        await _link.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _readReady.Dispose();
        _writeReady.Dispose();
        _pumpWake.Dispose();
        _sendWake.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    // ---- Stream boilerplate ----
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
}
