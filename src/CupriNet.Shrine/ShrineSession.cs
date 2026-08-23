using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Rites;
using CupriNet.Vessel;

// Namespace kept as CupriNet.Hosting although the type now ships in CupriNet.Shrine: the split exists so a Pilgrim
// need not drag the node-hosting stack (and its sockets) along, not to move anyone's type. CupriNet.Hosting forwards
// the type, so both source and binary references keep resolving.
namespace CupriNet.Hosting;

/// <summary>
/// A live Pilgrim-side visit to a Shrine: an encrypted, Signet-authenticated connection carrying the site rites at
/// once — the <b>Oracle</b> (request/response), the <b>Auspice</b> (live feeds) and the <b>Relic</b> (chunked bulk
/// content) — demultiplexed over one vessel, so a page can be fetched, its live data attended, and its app blob
/// pulled down on the same connection. Owns the vessel; disposing ends the visit.
/// </summary>
public sealed class ShrineSession : IAsyncDisposable
{
    private readonly IVessel _vessel;
    private readonly VesselMux _mux;
    private readonly OracleSession _oracle;

    internal ShrineSession(IVessel vessel, ICryptoSuite suite)
    {
        _vessel = vessel;
        // No Veil on any rite — the Pilgrimage vessel (Noise) is already the confidentiality + authenticity boundary.
        _mux = new VesselMux(vessel, ownsVessel: false);
        _oracle = new OracleSession(_mux.Stream(OracleSession.RequestStream));
        Auspices = new AuspiceSession(_mux.Stream(AuspiceSession.AuspiceStream));
        Relics = new RelicSession(_mux.Stream(RelicSession.RelicStream), suite);
    }

    /// <summary>The live-feed rite: attend a named Auspice and receive a snapshot then updates.</summary>
    public AuspiceSession Auspices { get; }

    /// <summary>The bulk-content rite: fetch a named relic chunk by chunk, each chunk verified against its manifest.</summary>
    public RelicSession Relics { get; }

    /// <summary>Consults the Shrine: sends an Oracle request and reads the response.</summary>
    public Task<OracleResponse> ConsultAsync(OracleRequest request, CancellationToken cancellationToken = default)
        => _oracle.ConsultAsync(request, cancellationToken);

    /// <summary>
    /// Attends a named live feed, yielding its snapshot and then each update until the Shrine seals it or the visit
    /// ends. Runs concurrently with <see cref="ConsultAsync"/> on the same connection.
    /// </summary>
    public IAsyncEnumerable<AuspiceFrame> AttendAsync(string topic, CancellationToken cancellationToken = default)
        => Auspices.AttendAllAsync(topic, cancellationToken);

    /// <summary>
    /// Fetches a single-file relic and returns its verified bytes — every chunk checked against the manifest as it
    /// arrives, the whole file checked before anything is returned, so the result is exactly what the Shrine's
    /// manifest promised (safe to execute, for an app blob). Runs concurrently with <see cref="ConsultAsync"/> and
    /// <see cref="AttendAsync"/> on the same connection. For a multi-file relic (a bundle) or a resumable transfer,
    /// use <see cref="Relics"/> directly.
    /// </summary>
    public async Task<byte[]> FetchRelicAsync(string name, long maxBytes = RelicSession.DefaultMaxFileBytes, CancellationToken cancellationToken = default)
    {
        var manifest = await Relics.FetchManifestAsync(name, cancellationToken).ConfigureAwait(false);
        if (manifest.Files.Count != 1)
            throw new RelicException(
                $"Relic '{name}' is a bundle of {manifest.Files.Count} files; fetch them through Relics.FetchFileAsync.");
        return await Relics.FetchFileAsync(name, manifest, fileIndex: 0, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _mux.DisposeAsync().ConfigureAwait(false);
        await _vessel.DisposeAsync().ConfigureAwait(false);
    }
}
