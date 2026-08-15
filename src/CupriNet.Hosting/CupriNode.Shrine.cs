using CupriNet.Abstractions;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Rites;
using CupriNet.Vessel;

namespace CupriNet.Hosting;

/// <summary>
/// A live Pilgrim-side visit to a Shrine: an encrypted, Signet-authenticated connection over which the Pilgrim
/// consults the site's Oracle rite. Owns the underlying (Noise) vessel; disposing it ends the visit.
/// </summary>
public sealed class ShrineSession : IAsyncDisposable
{
    private readonly IVessel _vessel;
    private readonly OracleSession _oracle;

    internal ShrineSession(IVessel vessel)
    {
        _vessel = vessel;
        // No Veil — the Pilgrimage vessel (Noise) is already the confidentiality + authenticity boundary.
        _oracle = new OracleSession(vessel, OracleSession.RequestStream);
    }

    /// <summary>Consults the Shrine: sends an Oracle request and reads the response.</summary>
    public Task<OracleResponse> ConsultAsync(OracleRequest request, CancellationToken cancellationToken = default)
        => _oracle.ConsultAsync(request, cancellationToken);

    public ValueTask DisposeAsync() => _vessel.DisposeAsync();
}

public sealed partial class CupriNode
{
    // The single Shrine this node hosts (v1). Multi-Shrine (per-connection Signet selection) is a later enhancement.
    private (Signet Signet, IOracleHandler Handler)? _shrine;

    /// <summary>
    /// Hosts a Shrine (an L2 site) under <paramref name="signet"/>, serving content through <paramref name="handler"/>.
    /// The Signet's <see cref="Signet.Address"/> (<c>cupri1…</c>) IS the site URL; a Pilgrim that dials it pins the
    /// Signet during the Pilgrimage, so the connection is self-authenticating — only the holder of the Signet key can
    /// answer, and the node's overlay Sigil is never presented (hosting a Shrine leaks no L1 identity). v1 hosts one
    /// Shrine per node, served to browser Pilgrims over the node's WebRTC endpoint.
    /// </summary>
    public void HostShrine(Signet signet, IOracleHandler handler)
    {
        ArgumentNullException.ThrowIfNull(signet);
        ArgumentNullException.ThrowIfNull(handler);
        _shrine = (signet, handler);
    }

    /// <summary>The <c>cupri1…</c> address of the Shrine this node hosts, or null if it hosts none.</summary>
    public string? ShrineAddress => _shrine?.Signet.Address;

    /// <summary>
    /// Host side of a Pilgrimage over an already-connected vessel: the Toll, then a Noise handshake in which the host
    /// presents ONLY the <paramref name="signet"/> (never the node Sigil), then the Oracle rite served through
    /// <paramref name="handler"/> until the Pilgrim disconnects. No overlay join — a Pilgrim is a short-lived client.
    /// On failure the vessel is disposed.
    /// </summary>
    public async Task AcceptPilgrimageOverVesselAsync(IVessel vessel, Signet signet, IOracleHandler handler, CancellationToken cancellationToken = default)
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

        try
        {
            // Serve over the now-encrypted vessel (no Veil — the Noise session is the security boundary).
            var oracle = new OracleSession(conjunction.Vessel, OracleSession.RequestStream);
            await OracleHost.ServeAsync(oracle, handler, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await conjunction.Vessel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pilgrim side of a Pilgrimage over an already-connected vessel: the Toll, then a Noise handshake that pins
    /// <paramref name="expectedSignet"/> (so a host that does not hold the site's key is rejected), under a throwaway
    /// per-visit identity that reveals nothing durable. Returns a <see cref="ShrineSession"/> to consult the Shrine's
    /// Oracle. No overlay join. On failure the vessel is disposed.
    /// </summary>
    public async Task<ShrineSession> PilgrimageOverVesselAsync(IVessel vessel, Sigil expectedSignet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        try
        {
            using var timed = LinkedHandshakeToken(cancellationToken);
            if (_options.EnableToll)
                await Toll.SolveAsync(vessel, timed.Token).ConfigureAwait(false);
            // Throwaway per-visit identity: the Pilgrim reveals no durable identity; pinning the Signet ensures only
            // the real site host (holding the key) completes the handshake.
            var pilgrim = NodeIdentity.Generate(Suite);
            var conjunction = await NoiseConjunction.InitiateAsync(
                vessel, pilgrim, Network, Suite, expectedPeer: expectedSignet, cancellationToken: timed.Token).ConfigureAwait(false);
            return new ShrineSession(conjunction.Vessel);
        }
        catch
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
