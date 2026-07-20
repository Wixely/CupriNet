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
    private readonly Vessel _vessel;
    private readonly bool _ownsVessel;
    private readonly ConcurrentDictionary<ushort, Channel<byte[]>> _streams = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    /// <summary>Starts pumping frames off the Vessel. If <paramref name="ownsVessel"/>, disposal also closes it.</summary>
    public VesselMux(Vessel vessel, bool ownsVessel = false)
    {
        _vessel = vessel ?? throw new ArgumentNullException(nameof(vessel));
        _ownsVessel = ownsVessel;
        _pump = PumpAsync(_cts.Token);
    }

    /// <summary>Returns the channel for a logical stream id (created on first use).</summary>
    public IStreamChannel Stream(ushort streamId) => new MuxStream(this, streamId, GetOrCreate(streamId).Reader);

    private Channel<byte[]> GetOrCreate(ushort streamId)
        => _streams.GetOrAdd(streamId, static _ => Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = false,
        }));

    private ValueTask SendAsync(ushort streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        => _vessel.SendAsync(streamId, payload, cancellationToken);

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var frame = await _vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                    break;
                GetOrCreate(frame.Value.StreamId).Writer.TryWrite(frame.Value.Payload);
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
