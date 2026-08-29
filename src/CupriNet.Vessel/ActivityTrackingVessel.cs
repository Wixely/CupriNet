using System.Net;

namespace CupriNet.Vessel;

/// <summary>
/// An <see cref="IVessel"/> that records when traffic last passed, in either direction, so a caller can hang up on a
/// connection that has gone quiet. Everything else is passed straight through.
///
/// <para><b>Both directions, deliberately.</b> Counting only what arrives would misread the most ordinary shape on a
/// Shrine: a Pilgrim attends a live feed, sends nothing more, and receives updates for as long as it cares to watch.
/// An inbound-only deadline would hang up on precisely the case the Auspice exists to serve. Counting both means
/// "idle" is what it sounds like — neither side has anything to say.</para>
///
/// <para>Uses <see cref="Environment.TickCount64"/> rather than wall-clock time, so a clock adjustment cannot make a
/// live connection look hours idle, nor an idle one look fresh.</para>
/// </summary>
public sealed class ActivityTrackingVessel(IVessel inner) : IVessel
{
    private readonly IVessel _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private long _lastActivityTicks = Environment.TickCount64;

    /// <summary>Milliseconds since a frame last passed in either direction.</summary>
    public long IdleMilliseconds => Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks);

    public EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
    public EndPoint? LocalEndPoint => _inner.LocalEndPoint;

    public ValueTask SendAsync(ushort streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        Touch();
        return _inner.SendAsync(streamId, payload, cancellationToken);
    }

    public async ValueTask<VesselFrame?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var frame = await _inner.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (frame is not null)
            Touch();
        return frame;
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);
}
