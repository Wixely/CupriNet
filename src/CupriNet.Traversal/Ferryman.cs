using CupriNet.Vessel;

namespace CupriNet.Traversal;

/// <summary>Live counts for a relay bridge.</summary>
public sealed class RelayCounters
{
    private long _forwarded;
    private long _dropped;

    /// <summary>Layer-1 frames forwarded.</summary>
    public long ForwardedL1 => Interlocked.Read(ref _forwarded);

    /// <summary>Layer-2 frames refused (never relayed).</summary>
    public long DroppedL2 => Interlocked.Read(ref _dropped);

    internal void CountForwarded() => Interlocked.Increment(ref _forwarded);

    internal void CountDropped() => Interlocked.Increment(ref _dropped);
}

/// <summary>
/// The Ferryman: an L1-only relay. It bridges two clients that cannot connect directly and forwards
/// frames between them — but ONLY on Layer-1 (Concordance) streams. Layer-2 (Arcanum) streams are
/// refused, which is what enforces the invariant that channel content is never relayed: two peers can
/// pair and run overlay discovery through a Ferryman, but cannot Consecrate a channel over it.
/// </summary>
/// <remarks>
/// Routing is by the frame's cleartext stream id only; when the bridged peers use Noise, their payloads
/// are end-to-end encrypted and the Ferryman never sees content.
/// </remarks>
public static class Ferryman
{
    // Layer-1 streams the Ferryman may carry:
    //   0 = transport/Noise handshake, 1 = peer-view exchange, 5 = reflexive-endpoint exchange.
    // Layer-2 streams it refuses: 2 = Consecration, 3 = Epistles, 4 = Conduits.
    private static readonly ushort[] Layer1Streams = [0, 1, 5];

    /// <summary>True if a stream may be relayed (Layer 1); false for Layer-2 channel streams.</summary>
    public static bool IsRelayable(ushort streamId) => Array.IndexOf(Layer1Streams, streamId) >= 0;

    /// <summary>
    /// Bridges two client vessels, forwarding Layer-1 frames in both directions until either side closes.
    /// Layer-2 frames are dropped (counted, never forwarded).
    /// </summary>
    public static async Task BridgeAsync(IVessel left, IVessel right, RelayCounters? counters = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var tally = counters ?? new RelayCounters();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leftToRight = ForwardAsync(left, right, tally, linked.Token);
        var rightToLeft = ForwardAsync(right, left, tally, linked.Token);

        await Task.WhenAny(leftToRight, rightToLeft).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(leftToRight, rightToLeft).ConfigureAwait(false);
        }
        catch
        {
            // teardown of the other direction after one side closed
        }
    }

    private static async Task ForwardAsync(IVessel from, IVessel to, RelayCounters counters, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            VesselFrame? frame;
            try
            {
                frame = await from.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (frame is null)
                break;

            if (IsRelayable(frame.Value.StreamId))
            {
                await to.SendAsync(frame.Value.StreamId, frame.Value.Payload, cancellationToken).ConfigureAwait(false);
                counters.CountForwarded();
            }
            else
            {
                counters.CountDropped(); // Layer-2 is never relayed
            }
        }
    }
}
