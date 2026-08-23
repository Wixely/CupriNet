using CupriNet.Abstractions;
using CupriNet.Rites;
using CupriNet.Vessel;

// Namespace kept as CupriNet.Hosting although the type now ships in CupriNet.Shrine: the split exists so a Pilgrim
// need not drag the node-hosting stack (and its sockets) along, not to move anyone's type. CupriNet.Hosting forwards
// the type, so both source and binary references keep resolving.
namespace CupriNet.Hosting;

/// <summary>
/// A live Pilgrim-side visit to a Shrine: an encrypted, Signet-authenticated connection carrying both site rites at
/// once — the <b>Oracle</b> (request/response) and the <b>Auspice</b> (live feeds) — demultiplexed over one vessel, so
/// a page can be fetched and its live data attended on the same connection. Owns the vessel; disposing ends the visit.
/// </summary>
public sealed class ShrineSession : IAsyncDisposable
{
    private readonly IVessel _vessel;
    private readonly VesselMux _mux;
    private readonly OracleSession _oracle;

    internal ShrineSession(IVessel vessel)
    {
        _vessel = vessel;
        // No Veil on either rite — the Pilgrimage vessel (Noise) is already the confidentiality + authenticity boundary.
        _mux = new VesselMux(vessel, ownsVessel: false);
        _oracle = new OracleSession(_mux.Stream(OracleSession.RequestStream));
        Auspices = new AuspiceSession(_mux.Stream(AuspiceSession.AuspiceStream));
    }

    /// <summary>The live-feed rite: attend a named Auspice and receive a snapshot then updates.</summary>
    public AuspiceSession Auspices { get; }

    /// <summary>Consults the Shrine: sends an Oracle request and reads the response.</summary>
    public Task<OracleResponse> ConsultAsync(OracleRequest request, CancellationToken cancellationToken = default)
        => _oracle.ConsultAsync(request, cancellationToken);

    /// <summary>
    /// Attends a named live feed, yielding its snapshot and then each update until the Shrine seals it or the visit
    /// ends. Runs concurrently with <see cref="ConsultAsync"/> on the same connection.
    /// </summary>
    public IAsyncEnumerable<AuspiceFrame> AttendAsync(string topic, CancellationToken cancellationToken = default)
        => Auspices.AttendAllAsync(topic, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _mux.DisposeAsync().ConfigureAwait(false);
        await _vessel.DisposeAsync().ConfigureAwait(false);
    }
}
