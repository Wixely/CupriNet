using CupriNet.Alembic;
using CupriNet.Rites;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.Hosting;

/// <summary>
/// A live, Consecrated channel session with a peer: Veil-encrypted Epistle (message) and Conduit (data)
/// rites over the paired Vessel. The message and data rites use distinct logical streams (3 and 4).
/// </summary>
/// <remarks>
/// Reading Epistles and Conduits concurrently on one session needs a stream demultiplexer (a later
/// addition); today, read one rite at a time per Vessel.
/// </remarks>
public sealed class ArcanumSession
{
    internal ArcanumSession(VesselSession vessel, long epoch, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite)
    {
        Epoch = epoch;
        Epistles = new EpistleSession(vessel, sessionKey, suite);
        Conduits = new ConduitSession(vessel, sessionKey, suite);
    }

    /// <summary>The epoch the Consecration was bound to.</summary>
    public long Epoch { get; }

    /// <summary>The message rite (Epistles + Attestations).</summary>
    public EpistleSession Epistles { get; }

    /// <summary>The generic data rite (Conduits).</summary>
    public ConduitSession Conduits { get; }

    /// <summary>Sends a UTF-8 text Epistle.</summary>
    public Task SendTextAsync(string text, DateTimeOffset now, CancellationToken cancellationToken = default)
        => Epistles.SendMessageAsync(Epistle.Text(text, now), cancellationToken);
}
