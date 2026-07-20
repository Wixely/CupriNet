using System.Net;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Core;

namespace CupriNet.Hosting;

/// <summary>Configuration for a <see cref="CupriNode"/>.</summary>
public sealed record CupriNodeOptions
{
    /// <summary>The network (Concordance) this node belongs to.</summary>
    public required string Concordium { get; init; }

    /// <summary>TCP port to listen on (0 = OS-assigned ephemeral port).</summary>
    public int ListenPort { get; init; }

    /// <summary>Address to bind. Defaults to loopback; use <see cref="IPAddress.Any"/> for all interfaces.</summary>
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;

    /// <summary>
    /// Crypto suite. Defaults to the secure BouncyCastle suite; pass the Simulacrum (with consent) only
    /// for development.
    /// </summary>
    public ICryptoSuite? Suite { get; init; }

    /// <summary>Secret store for identity/relationships. Defaults to in-memory (non-persistent).</summary>
    public ISecretStore? SecretStore { get; init; }

    /// <summary>Reachability candidates to advertise in Intonations. Defaults to the bound endpoint.</summary>
    public IReadOnlyList<Beacon>? AdvertisedBeacons { get; init; }

    /// <summary>
    /// Run a reflexive-endpoint exchange during pairing so the node learns its externally-observed
    /// (Mapped) address. Requires both peers to support it. Default true.
    /// </summary>
    public bool EnableReflexiveDiscovery { get; init; } = true;

    /// <summary>
    /// After a channel pairing, exchange signed self-records so each side seeds the other into its
    /// Constellation. This bootstraps the overlay from a single link: a node that Conjoins an Intonation
    /// then knows the inviter (and can route overlay lookups through it). Best-effort; default true.
    /// </summary>
    public bool EnablePeerExchange { get; init; } = true;

    /// <summary>
    /// Require every channel message/frame to carry a valid author signature, and reject unsigned ones.
    /// Channel sessions always <em>sign</em> outbound content with this node's Seal; this flag controls
    /// whether inbound unsigned content is refused. Recommended <c>true</c> for multi-party channels where
    /// content is relayed peer-to-peer (so a relaying member cannot forge authorship). Default true.
    /// </summary>
    public bool RequireSignedAuthors { get; init; } = true;

    /// <summary>Maximum time a channel Consecration handshake may take before it is abandoned.</summary>
    public TimeSpan ConsecrationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Require a pre-handshake stateless cookie (the "Toll") before the Noise transport handshake. The
    /// responder keeps no per-connection state to validate it, so an attacker cannot exhaust responder
    /// memory by opening connections and stalling before the expensive crypto. Both peers must agree;
    /// default true. Disable only for interop with peers that predate the Toll.
    /// </summary>
    public bool EnableToll { get; init; } = true;
}
