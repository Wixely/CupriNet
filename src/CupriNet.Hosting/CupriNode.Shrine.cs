using CupriNet.Abstractions;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Rites;
using CupriNet.Vessel;

namespace CupriNet.Hosting;

public sealed partial class CupriNode
{
    private static readonly IReadOnlyDictionary<string, IAuspiceSource> NoFeeds =
        new Dictionary<string, IAuspiceSource>();

    // The single Shrine this node hosts (v1). Multi-Shrine (per-connection Signet selection) is a later enhancement.
    private (Signet Signet, IOracleHandler Handler, IReadOnlyDictionary<string, IAuspiceSource> Feeds, IRelicSource? Relics, bool Advertise)? _shrine;

    /// <summary>
    /// Hosts a Shrine (an L2 site) under <paramref name="signet"/>, serving content through <paramref name="handler"/>
    /// and optional live feeds through <paramref name="feeds"/> (keyed by feed name). The Signet's
    /// <see cref="Signet.Address"/> (<c>cupri1…</c>) IS the site URL; a Pilgrim that dials it pins the Signet during the
    /// Pilgrimage, so the connection is self-authenticating — only the holder of the Signet key can answer, and the
    /// node's overlay Sigil is never presented (hosting a Shrine leaks no L1 identity). v1 hosts one Shrine per node,
    /// served to browser Pilgrims over the node's WebRTC endpoint.
    /// <para>
    /// <paramref name="advertiseInLink"/> stamps the Shrine's Signet into every Intonation this node mints, so
    /// "here is my link" doubles as "here is my site". It is <b>off by default</b> because it ties the Shrine to this
    /// node's overlay Sigil — right for a public or branded host, wrong for anonymous hosting.
    /// </para>
    /// </summary>
    public void HostShrine(Signet signet, IOracleHandler handler, bool advertiseInLink = false)
        => HostShrine(signet, handler, feeds: null, advertiseInLink);

    /// <summary>
    /// As <see cref="HostShrine(Signet, IOracleHandler, bool)"/>, plus named live <paramref name="feeds"/> the Shrine
    /// emanates over the Auspice rite (see <see cref="IAuspiceSource"/>).
    /// </summary>
    public void HostShrine(
        Signet signet,
        IOracleHandler handler,
        IReadOnlyDictionary<string, IAuspiceSource>? feeds,
        bool advertiseInLink = false)
        => HostShrine(signet, handler, feeds, relics: null, advertiseInLink);

    /// <summary>
    /// As <see cref="HostShrine(Signet, IOracleHandler, IReadOnlyDictionary{string, IAuspiceSource}, bool)"/>, plus
    /// named bulk <paramref name="relics"/> the Shrine serves over the Relic rite — chunked and hash-verified, the
    /// only way content past the message ceiling (an app blob, an asset pack) reaches a Pilgrim
    /// (see <see cref="IRelicSource"/> / <see cref="StaticRelicSource"/>).
    /// </summary>
    public void HostShrine(
        Signet signet,
        IOracleHandler handler,
        IReadOnlyDictionary<string, IAuspiceSource>? feeds,
        IRelicSource? relics,
        bool advertiseInLink = false)
    {
        ArgumentNullException.ThrowIfNull(signet);
        ArgumentNullException.ThrowIfNull(handler);
        _shrine = (signet, handler, feeds ?? NoFeeds, relics, advertiseInLink);
    }

    /// <summary>The <c>cupri1…</c> address of the Shrine this node hosts, or null if it hosts none.</summary>
    public string? ShrineAddress => _shrine?.Signet.Address;

    /// <summary>The Shrine Signet to stamp into a minted Intonation — only when hosting one AND opted in.</summary>
    internal Sigil? ShrineToAdvertise => _shrine is { Advertise: true } shrine ? shrine.Signet.Sigil : null;

    /// <summary>
    /// Host side of a Pilgrimage over an already-connected vessel: the Toll, then a Noise handshake in which the host
    /// presents ONLY the <paramref name="signet"/> (never the node Sigil), then both site rites — the Oracle served by
    /// <paramref name="handler"/> and any live <paramref name="feeds"/> — until the Pilgrim disconnects. No overlay
    /// join: a Pilgrim is a short-lived client. On failure the vessel is disposed.
    /// </summary>
    public Task AcceptPilgrimageOverVesselAsync(
        IVessel vessel, Signet signet, IOracleHandler handler, CancellationToken cancellationToken = default)
        => AcceptPilgrimageOverVesselAsync(vessel, signet, handler, feeds: null, cancellationToken);

    /// <summary>
    /// As <see cref="AcceptPilgrimageOverVesselAsync(IVessel, Signet, IOracleHandler, CancellationToken)"/>, also
    /// serving named live <paramref name="feeds"/> over the Auspice rite alongside the Oracle.
    /// </summary>
    public Task AcceptPilgrimageOverVesselAsync(
        IVessel vessel,
        Signet signet,
        IOracleHandler handler,
        IReadOnlyDictionary<string, IAuspiceSource>? feeds,
        CancellationToken cancellationToken = default)
        => AcceptPilgrimageOverVesselAsync(vessel, signet, handler, feeds, relics: null, cancellationToken);

    /// <summary>
    /// As <see cref="AcceptPilgrimageOverVesselAsync(IVessel, Signet, IOracleHandler, IReadOnlyDictionary{string, IAuspiceSource}, CancellationToken)"/>,
    /// also serving named bulk <paramref name="relics"/> over the Relic rite alongside the Oracle and the Auspice.
    /// </summary>
    public async Task AcceptPilgrimageOverVesselAsync(
        IVessel vessel,
        Signet signet,
        IOracleHandler handler,
        IReadOnlyDictionary<string, IAuspiceSource>? feeds,
        IRelicSource? relics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        ArgumentNullException.ThrowIfNull(signet);
        ArgumentNullException.ThrowIfNull(handler);

        NoiseConjunctionResult conjunction;
        try
        {
            using var timed = LinkedHandshakeToken(cancellationToken);
            if (_options.EnableToll)
                await Toll.IssueAndVerifyAsync(vessel, _tollSecret, vessel.RemoteEndPoint, DateTimeOffset.UtcNow, timed.Token).ConfigureAwait(false);
            // Present the Signet as this session's identity: the Pilgrim pins it (self-authenticating), and nothing
            // about the node's overlay identity is revealed.
            conjunction = await NoiseConjunction.AcceptAsync(vessel, NodeIdentity.FromSeal(signet.Seal), Network, Suite, timed.Token).ConfigureAwait(false);
        }
        catch
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // Serve both rites over the now-encrypted vessel, demultiplexed so a fetch and a live feed run concurrently.
        using var serving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var mux = new VesselMux(conjunction.Vessel, ownsVessel: false);
        var loops = new[]
        {
            OracleHost.ServeAsync(new OracleSession(mux.Stream(OracleSession.RequestStream)), handler, serving.Token),
            AuspiceHost.ServeAsync(new AuspiceSession(mux.Stream(AuspiceSession.AuspiceStream)), feeds ?? NoFeeds, serving.Token),
            RelicHost.ServeAsync(new RelicSession(mux.Stream(RelicSession.RelicStream), Suite), relics, serving.Token),
        };

        try
        {
            // Any loop ending means the visit is over (the Pilgrim closed, or a rite failed).
            await Task.WhenAny(loops).ConfigureAwait(false);
        }
        finally
        {
            await serving.CancelAsync().ConfigureAwait(false);
            // Observe both outcomes so neither faults unobserved; cancellation here is the expected path.
            try { await Task.WhenAll(loops).ConfigureAwait(false); } catch { /* teardown */ }
            await mux.DisposeAsync().ConfigureAwait(false);
            await conjunction.Vessel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pilgrim side of a Pilgrimage over an already-connected vessel: the Toll, then a Noise handshake that pins
    /// <paramref name="expectedSignet"/> (so a host that does not hold the site's key is rejected), under a throwaway
    /// per-visit identity that reveals nothing durable. Returns a <see cref="ShrineSession"/> carrying both the Oracle
    /// and Auspice rites. No overlay join. On failure the vessel is disposed.
    /// <para>
    /// Convenience over <see cref="Pilgrimage.OverVesselAsync"/>, which needs no node at all — use that one from a
    /// browser, a CLI, or a test, where constructing a node (and its listener) is impossible or unwanted.
    /// </para>
    /// </summary>
    public Task<ShrineSession> PilgrimageOverVesselAsync(IVessel vessel, Sigil expectedSignet, CancellationToken cancellationToken = default)
        => Pilgrimage.OverVesselAsync(vessel, expectedSignet, Network, Suite, _options.EnableToll, cancellationToken);
}
