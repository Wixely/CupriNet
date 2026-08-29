using System.Net;

namespace CupriNet.Vessel;

/// <summary>
/// A connected, multiplexed transport session: send and receive stream-tagged frames. Implemented by the
/// raw <see cref="Vessel"/> (plaintext TCP) and by wrappers such as a Noise-encrypting vessel, so higher
/// layers work over either without change.
/// </summary>
public interface IVessel : IAsyncDisposable
{
    /// <summary>The remote peer's endpoint, if connected.</summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>This side's local endpoint, if connected.</summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>
    /// Sends a payload on a logical stream.
    ///
    /// <para><b>An implementation owes no write serialisation.</b> Several provide it, each for a reason of its own —
    /// a byte-stream <see cref="Vessel"/> must, or two frames would interleave into one unparseable stream; a
    /// Noise-encrypting vessel must, or the nonce order would drift from the wire order. A message transport whose
    /// boundaries are preserved for it need not. <b>Do not read the guarantee off whichever implementation you happen
    /// to be looking at</b>: a caller that sends from several tasks is responsible for serialising them, the way
    /// <c>AuspiceSession</c>, <c>RelicSession</c> and <c>ConduitSession</c> each do with a lock of their own.
    /// (<c>OracleSession</c> holds none and needs none — its exchange carries no correlation id, so it is
    /// single-flight by design and concurrent consults would take each other's responses whatever the transport did.)</para>
    /// </summary>
    ValueTask SendAsync(ushort streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives the next frame, or null when the peer closes the connection.
    ///
    /// <para>Single-consumer. On a multiplexed connection that consumer is the <see cref="VesselMux"/> pump, which
    /// owns the receive loop and routes each frame to its stream — so protocols read concurrently from their own
    /// streams without racing here.</para>
    /// </summary>
    ValueTask<VesselFrame?> ReceiveAsync(CancellationToken cancellationToken = default);
}
