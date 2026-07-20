using CupriNet.Alembic;
using CupriNet.Rites;
using CupriNet.Vessel;
using VesselSession = CupriNet.Vessel.IVessel;

namespace CupriNet.Hosting;

/// <summary>
/// A live, Consecrated channel session with a peer: Veil-encrypted Epistle (message) and Conduit (data)
/// rites over the paired Vessel. A <see cref="VesselMux"/> demultiplexes the connection, so the two rites
/// use distinct streams (3 and 4) and can be read concurrently. Disposing stops the demux pump; the
/// underlying Vessel is owned by the <see cref="PairedPeer"/>.
/// </summary>
public sealed class ArcanumSession : IAsyncDisposable
{
    private readonly VesselMux _mux;

    internal ArcanumSession(VesselSession vessel, long epoch, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite,
        RiteIdentity author, bool requireSignedAuthors)
    {
        Epoch = epoch;
        _mux = new VesselMux(vessel, ownsVessel: false);
        Epistles = new EpistleSession(_mux.Stream(EpistleSession.ContentStream), sessionKey, suite, author, requireSignedAuthors);
        Conduits = new ConduitSession(_mux.Stream(ConduitSession.DataStream), sessionKey, suite, author, requireSignedAuthors);
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

    public ValueTask DisposeAsync() => _mux.DisposeAsync();
}
