using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Rites;
using CupriNet.Vessel;

namespace CupriNet.Hosting;

/// <summary>
/// The Pilgrim side of a Shrine visit, free of any node. A Pilgrim mints a throwaway identity per visit and skips the
/// overlay join entirely, so nothing here needs node state — which is the point: this is reachable from a browser
/// (WebAssembly), a CLI that only wants to fetch a page, a test with no overlay, or anything embedded without a
/// listener. <c>CupriNode.PilgrimageOverVesselAsync</c> is the same rite for callers that already have a node.
/// </summary>
public static class Pilgrimage
{
    /// <summary>
    /// Maximum time the Pilgrimage handshake (Toll + Noise) may take. Mirrors the node's handshake budget; a Pilgrim
    /// runs the same wire exchange, so the two must not drift apart.
    /// </summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Visits a Shrine over an already-connected <paramref name="vessel"/>: the Toll, then a Noise handshake that pins
    /// <paramref name="expectedSignet"/> (so a host that does not hold the site's key is rejected), under a throwaway
    /// per-visit identity that reveals nothing durable. Returns a <see cref="ShrineSession"/> carrying both site rites
    /// — the Oracle and the Auspice. No overlay join. On failure the vessel is disposed.
    /// </summary>
    /// <param name="vessel">A connected transport to the host (TCP, WebRTC data channel, an in-memory pair in tests).</param>
    /// <param name="expectedSignet">The Shrine's Signet Sigil, i.e. its <c>cupri1…</c> address — pinned during the handshake.</param>
    /// <param name="network">The Concordium this Shrine belongs to; must match the host's.</param>
    /// <param name="suite">The crypto suite to run the handshake under.</param>
    /// <param name="enableToll">Whether the host demands the Toll before the handshake; must match the host's setting.</param>
    public static async Task<ShrineSession> OverVesselAsync(
        IVessel vessel,
        Sigil expectedSignet,
        Concordium network,
        ICryptoSuite suite,
        bool enableToll = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        RiteTransport.EnsureCarriesRiteFrames(vessel);
        try
        {
            // Inside the try: every failure past this point owes the caller a disposed vessel.
            ArgumentNullException.ThrowIfNull(suite);
            using var timed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timed.CancelAfter(HandshakeTimeout);
            if (enableToll)
                await Toll.SolveAsync(vessel, timed.Token).ConfigureAwait(false);
            // Throwaway per-visit identity: the Pilgrim reveals no durable identity; pinning the Signet ensures only
            // the real site host (holding the key) completes the handshake.
            var pilgrim = NodeIdentity.Generate(suite);
            // Name the Shrine as well as pinning it: an endpoint may host several, and `target` is how it learns
            // which one to answer as. It travels blinded and inside Noise, so neither the network nor an endpoint
            // that does not host it learns which site was wanted.
            var conjunction = await NoiseConjunction.InitiateAsync(
                vessel, pilgrim, network, suite, expectedPeer: expectedSignet, cancellationToken: timed.Token,
                target: expectedSignet).ConfigureAwait(false);
            return new ShrineSession(conjunction.Vessel, suite);
        }
        catch
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
