using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CupriNet.Vessel;

/// <summary>
/// Demultiplexes a single <see cref="Vessel"/> into independent per-stream channels. One background pump
/// owns the Vessel's receive loop and routes each frame to its stream's queue, so multiple protocols
/// (Epistles, Conduits, …) can be read concurrently off one connection without racing on receive.
/// </summary>
public sealed class VesselMux : IAsyncDisposable
{
    /// <summary>Maximum number of distinct logical streams demultiplexed from one connection (a Ward).</summary>
    public const int DefaultMaxConcurrentStreams = 64;

    /// <summary>Maximum frames buffered per stream before inbound frames are dropped (a Ward).</summary>
    public const int DefaultMaxQueuedFramesPerStream = 1024;

    private readonly IVessel _vessel;
    private readonly bool _ownsVessel;
    private readonly int _maxStreams;
    private readonly int _maxQueuePerStream;
    private readonly ConcurrentDictionary<ushort, Channel<byte[]>> _streams = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    /// <summary>Starts pumping frames off the Vessel. If <paramref name="ownsVessel"/>, disposal also closes it.</summary>
    public VesselMux(IVessel vessel, bool ownsVessel = false,
        int maxConcurrentStreams = DefaultMaxConcurrentStreams, int maxQueuedFramesPerStream = DefaultMaxQueuedFramesPerStream)
    {
        _vessel = vessel ?? throw new ArgumentNullException(nameof(vessel));
        _ownsVessel = ownsVessel;
        _maxStreams = maxConcurrentStreams;
        _maxQueuePerStream = maxQueuedFramesPerStream;
        _pump = PumpAsync(_cts.Token);
    }

    /// <summary>Returns the channel for a logical stream id (created on first use). Local (trusted) callers are not Ward-capped.</summary>
    public IStreamChannel Stream(ushort streamId) => new MuxStream(this, streamId, GetOrCreate(streamId).Reader);

    private Channel<byte[]> GetOrCreate(ushort streamId)
        => _streams.GetOrAdd(streamId, _ => Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_maxQueuePerStream)
        {
            SingleWriter = true,
            SingleReader = false,
            // Bound per-stream memory: under a flood, drop the newest frame rather than grow without limit.
            // The reliable rites (Vigil acks/retries) recover any Epistle dropped this way.
            FullMode = BoundedChannelFullMode.DropWrite,
        }));

    private ValueTask SendAsync(ushort streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        => _vessel.SendAsync(streamId, payload, cancellationToken);

    /// <summary>Routes an inbound frame, enforcing the concurrent-stream Ward against a hostile peer.</summary>
    private void RouteInbound(ushort streamId, byte[] payload)
    {
        if (_streams.TryGetValue(streamId, out var existing))
        {
            existing.Writer.TryWrite(payload);
            return;
        }

        // A stream id the peer has not used before: only admit it while under the Ward, so a peer cannot
        // force the creation of an unbounded number of per-stream queues.
        if (_streams.Count >= _maxStreams)
            return;
        GetOrCreate(streamId).Writer.TryWrite(payload);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var frame = await _vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                    break;
                RouteInbound(frame.Value.StreamId, frame.Value.Payload);
            }

            CompleteAll(exception: null);
        }
        catch (OperationCanceledException)
        {
            CompleteAll(exception: null);
        }
        catch (Exception ex)
        {
            CompleteAll(ex);
        }
    }

    private void CompleteAll(Exception? exception)
    {
        foreach (var channel in _streams.Values)
            channel.Writer.TryComplete(exception);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch
        {
            // pump faults on cancellation / closed vessel are expected during teardown
        }

        if (_ownsVessel)
            await _vessel.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private sealed class MuxStream(VesselMux mux, ushort streamId, ChannelReader<byte[]> reader) : IStreamChannel
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => mux.SendAsync(streamId, payload, cancellationToken);

        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null; // connection closed
            }
        }
    }
}
