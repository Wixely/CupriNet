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
    /// Cache the overlay view (known nodes) to the secret store and reload it on startup — a warm start that
    /// lets the node reconnect to peers it already knows directly, avoiding cold-start discovery hops.
    /// <para>
    /// <em>On by default</em> (a warm/"hot" start): known nodes are kept on disk so reconnection is fast and
    /// the local network map survives restarts. Set it to <c>false</c> for a <em>cold start</em> — nothing
    /// about the overlay is written to disk, which is better for plausible deniability (there is no local
    /// record of which nodes/channels you have discovered). Expose this opt-out to the end user.
    /// </para>
    /// <para>
    /// Only has effect with a persistent <see cref="SecretStore"/>; with the default in-memory store there is
    /// nothing durable to write to, so leaving this on is harmless there.
    /// </para>
    /// </summary>
    public bool PersistOverlay { get; init; } = true;

    /// <summary>
    /// Continuously gossip with a few random known nodes to keep the local network map fresh — and, as a
    /// side effect, to <em>fuzz</em> our connection pattern: because we are always contacting random nodes,
    /// an observer cannot pick out the connection we actually care about (e.g. a channel member) from the
    /// decoys. On by default. Each round also discovers new nodes (from the samples we pull), growing the map.
    /// </summary>
    public bool EnableOverlayGossip { get; init; } = true;

    /// <summary>How often (seconds) a gossip round runs.</summary>
    public int OverlayGossipIntervalSeconds { get; init; } = 60;

    /// <summary>How many random known nodes a gossip round contacts (the decoy/fanout count).</summary>
    public int OverlayGossipFanout { get; init; } = 3;

    /// <summary>How many peer records to pull from each gossiped node.</summary>
    public int OverlayGossipSampleSize { get; init; } = 16;

    /// <summary>
    /// Power/connectivity profile. Gates battery- and data-costly behaviour: hot fuzz (holding long-lived
    /// decoy connections open) is suppressed on <see cref="PowerProfile.Metered"/>. Default Unmetered.
    /// </summary>
    public PowerProfile Power { get; init; } = PowerProfile.Unmetered;

    /// <summary>
    /// "Hot fuzz": hold a small set of long-lived decoy control connections open to random overlay nodes,
    /// paced with padded cover traffic, so a real channel session — also long-lived and chatty — blends into a
    /// population of equally long-lived, equally chatty decoys. Where <see cref="EnableOverlayGossip"/> fuzzes
    /// connection <em>events</em>, hot fuzz fuzzes connection <em>lifetime and volume</em>, closing the
    /// traffic-analysis tell that "the enduring connection is the real one". Runs only alongside gossip and on
    /// an <see cref="PowerProfile.Unmetered"/> profile; on by default there, off when metered.
    /// </summary>
    public bool EnableHotFuzz { get; init; } = true;

    /// <summary>How many long-lived decoy companions hot fuzz keeps warm at once.</summary>
    public int HotFuzzDegree { get; init; } = 4;

    /// <summary>Shortest a companion is held before rotation — the low end of the ordinary TTL band.</summary>
    public TimeSpan HotFuzzMinHold { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Upper end of the ordinary TTL band a companion is held before rotation.</summary>
    public TimeSpan HotFuzzMaxHold { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Probability a companion instead draws a <em>long</em> hold (up to <see cref="HotFuzzLongHold"/>). This
    /// heavy tail is deliberate: without a few hours-long decoys, an hours-long channel session would again be
    /// the unique connection that never rotates.
    /// </summary>
    public double HotFuzzLongHoldProbability { get; init; } = 0.2;

    /// <summary>The upper bound of the heavy-tail hold, drawn with <see cref="HotFuzzLongHoldProbability"/>.</summary>
    public TimeSpan HotFuzzLongHold { get; init; } = TimeSpan.FromHours(4);

    /// <summary>Base interval between heartbeats (padded PINGs) on each companion; actual timing is jittered.</summary>
    public TimeSpan HotFuzzHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Cooperative padding: each heartbeat carries random padding and asks the companion to pad its reply to a
    /// target size, so both directions of a decoy link are shaped to resemble a bidirectional chat rather than
    /// a thin request/response. On by default.
    /// </summary>
    public bool HotFuzzCooperativePadding { get; init; } = true;

    /// <summary>Target padding (bytes) per direction of a hot-fuzz heartbeat when cooperative padding is on.</summary>
    public int HotFuzzPaddingBytes { get; init; } = 256;

    /// <summary>
    /// Effigies: decoy <em>channel</em> sessions. Where hot fuzz shapes L1 control links, an Effigy is a full
    /// L2-shaped session — a direct, chat-shaped connection to a cooperating partner over a throwaway coordinate
    /// that is never published, persisted, or joinable — so a real channel session blends among decoys that
    /// carry human-like conversation traffic. Off by default: unlike hot fuzz's cheap heartbeats, Effigies push
    /// continuous cover traffic, so this is real bandwidth. Runs only on an <see cref="PowerProfile.Unmetered"/>
    /// profile.
    /// </summary>
    public bool EnableEffigies { get; init; }

    /// <summary>How many independent one-to-one Effigy sessions to hold (each with its own conversation shape).</summary>
    public int EffigyCount { get; init; } = 2;

    /// <summary>
    /// Size of one <em>coordinated</em> Effigy cohort. A real group channel is a mesh of direct Vessels that
    /// light up together when you post; a cohort reproduces that fan-out by bursting to all its members at once,
    /// covering the multi-party correlation a set of independent Effigies cannot. 0 disables group cover.
    /// </summary>
    public int EffigyGroupSize { get; init; }

    /// <summary>Upper bound (bytes) on a single Effigy cover message; sizes are drawn skewed-small below it.</summary>
    public int EffigyMaxMessageBytes { get; init; } = 512;

    /// <summary>
    /// Pageants: negotiated <em>fake groups</em> (decoy cliques). Where an Effigy cohort is a star (you fan out to
    /// unconnected decoys), a Pageant is a full mesh whose members run one shared conversation schedule, so it
    /// reproduces the clique topology and turn-taking of a real group channel — the tell a star cannot. This node
    /// initiates and maintains its own Pageants only when set; off by default (fan-out cost is quadratic in group
    /// size). Runs only on an <see cref="PowerProfile.Unmetered"/> profile. Participating in <em>others'</em>
    /// Pageants when invited is governed separately by <see cref="MaxPageantsAsMember"/>.
    /// </summary>
    public bool EnablePageants { get; init; }

    /// <summary>How many Pageants this node initiates and keeps alive (self-healing to <see cref="PageantSize"/>).</summary>
    public int PageantCount { get; init; } = 1;

    /// <summary>Target member count of a Pageant this node forms (including itself). Clamped to the roster cap.</summary>
    public int PageantSize { get; init; } = 4;

    /// <summary>
    /// Max Pageants this node will join at others' invitation (0 refuses all — it won't act as anyone's decoy
    /// member). Bounds the bandwidth a node can be conscripted into. Only honored on an unmetered profile.
    /// <para>
    /// <em>Off (0) by default</em>: accepting an invite makes this node dial the invite's other roster members,
    /// whose addresses the inviter supplies — a crafted invite could otherwise direct connections at
    /// attacker-chosen hosts. Like <see cref="EnablePageants"/> (initiating), participating in others' Pageants
    /// is opt-in. When enabled, inbound Pageant edges are held under the per-peer control budget and dead members
    /// are re-negotiated out, bounding the exposure.
    /// </para>
    /// </summary>
    public int MaxPageantsAsMember { get; init; }

    /// <summary>Max inbound overlay-control connections served concurrently across all peers — a Ward against connection floods.</summary>
    public int MaxConcurrentControlConnections { get; init; } = 256;

    /// <summary>
    /// Max inbound connections whose pre-authentication handshake (Toll + Noise) may be in progress at once. The
    /// accept loop gates on a free slot before accepting, so a flood — or a batch of slow "slow-loris" peers that
    /// connect and then stall — queues in the kernel backlog and can never stall the loop; each stalled handshake
    /// also self-expires at the handshake timeout.
    /// </summary>
    public int MaxConcurrentHandshakes { get; init; } = 64;

    /// <summary>Max concurrent overlay-control connections from a single peer (Sigil), so one peer cannot multiply its budget.</summary>
    public int MaxControlConnectionsPerPeer { get; init; } = 8;

    /// <summary>Max overlay-control requests from a single peer within <see cref="ControlWindowSeconds"/>, shared across that peer's connections.</summary>
    public int MaxControlRequestsPerWindow { get; init; } = 120;

    /// <summary>The rolling window (seconds) for the per-peer overlay-control request limit.</summary>
    public int ControlWindowSeconds { get; init; } = 10;

    /// <summary>
    /// Proof-of-work difficulty (leading zero bits) this node grinds when publishing a channel advert
    /// (the Tribute). Clamped to <see cref="CupriNet.Concordance.Tribute.MaxDifficulty"/> — the payer's
    /// ceiling. Low by default so it is present but cheap; raise it once calibrated for your devices.
    /// </summary>
    public int TributeDifficulty { get; init; } = 8;

    /// <summary>
    /// Proof-of-work difficulty this node <em>requires</em> on an inbound channel advert before storing it
    /// (a local receiver policy — raise it to demand more work, never negotiated down by the publisher).
    /// </summary>
    public int RequiredTributeDifficulty { get; init; } = 8;

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
    /// Per-candidate TCP connect timeout when dialing a peer's beacons in turn, so an unreachable candidate (e.g.
    /// a private/LAN address dialed from off-LAN, which can black-hole) is abandoned quickly and the next tried.
    /// </summary>
    public TimeSpan CandidateConnectTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Require a pre-handshake stateless cookie (the "Toll") before the Noise transport handshake. The
    /// responder keeps no per-connection state to validate it, so an attacker cannot exhaust responder
    /// memory by opening connections and stalling before the expensive crypto. Both peers must agree;
    /// default true. Disable only for interop with peers that predate the Toll.
    /// </summary>
    public bool EnableToll { get; init; } = true;

    /// <summary>
    /// Announce this node's presence on the local network and discover same-network peers, so two nodes on one
    /// LAN can pair with no link and no NAT at all — the easiest genesis path. Off by default (it broadcasts);
    /// apps opt in. Discovered peers surface via <c>LanPeerDiscovered</c> and pair via <c>ConjoinDiscoveredAsync</c>.
    /// </summary>
    public bool EnableLanDiscovery { get; init; }

    /// <summary>UDP port for LAN presence announcements (both the bind and the broadcast target).</summary>
    public int LanDiscoveryPort { get; init; } = 43821;

    /// <summary>How often (seconds) to broadcast a LAN presence announcement.</summary>
    public int LanAnnounceIntervalSeconds { get; init; } = 5;

    /// <summary>
    /// Explicit LAN announcement targets. Defaults to the subnet broadcast on <see cref="LanDiscoveryPort"/>;
    /// override to point announcements at specific endpoints (e.g. for deterministic tests on loopback).
    /// </summary>
    public IReadOnlyList<IPEndPoint>? LanAnnounceTargets { get; init; }

    /// <summary>
    /// Ask the home gateway (NAT-PMP) to forward this node's listen port automatically, so it becomes directly
    /// reachable with no manual port-forwarding — the mapping surfaces as a Mapped beacon in generated links.
    /// Off by default (it talks to the gateway); apps opt in. No-op where the router doesn't support NAT-PMP.
    /// </summary>
    public bool EnablePortMapping { get; init; }

    /// <summary>Requested lifetime (seconds) of a NAT-PMP mapping; it is renewed at half this interval.</summary>
    public int PortMappingLifetimeSeconds { get; init; } = 3600;

    /// <summary>
    /// Optional onion (Tor) transport. When set, the node publishes an onion service (advertised as an
    /// <see cref="CupriNet.Core.EndpointKind.Onion"/> beacon) and can dial peers' <c>.onion</c> addresses, all
    /// over the ordinary pairing seams. Supplied by the app so <c>CupriNet.Hosting</c> needs no Tor dependency.
    /// <para>
    /// Requires a durable <see cref="SecretStore"/>: Tor's entry guards must survive restart, and reselecting
    /// guards each run is a deanonymization risk — so Tor is deliberately incompatible with a cold start
    /// (in-memory / no store). Enabling it without a persistent store is rejected at creation.
    /// </para>
    /// </summary>
    public IOnionTransport? OnionTransport { get; init; }

    /// <summary>
    /// How this node is reachable, and — critically — what it will and won't do to preserve anonymity.
    /// <see cref="ReachabilityMode.Standard"/> uses clearnet transports (and Tor too if
    /// <see cref="OnionTransport"/> is set). <see cref="ReachabilityMode.TorOnly"/> enforces anonymity: the
    /// listener binds to loopback only, LAN discovery / port mapping / reflexive learning are off, only the onion
    /// address is advertised, and the node dials <em>only</em> onion addresses — so a Tor identity can never leak
    /// its IP by touching clearnet. TorOnly requires an <see cref="OnionTransport"/>.
    /// </summary>
    public ReachabilityMode Mode { get; init; } = ReachabilityMode.Standard;
}

/// <summary>Whether a node uses clearnet transports, or is strictly Tor-only for anonymity. See <see cref="CupriNodeOptions.Mode"/>.</summary>
public enum ReachabilityMode
{
    /// <summary>Clearnet transports (LAN, direct, hole punch, NAT-PMP), plus Tor if an OnionTransport is supplied.</summary>
    Standard,

    /// <summary>Strict anonymity: onion-only reachability and dialing; all clearnet paths disabled.</summary>
    TorOnly,
}

/// <summary>Power/connectivity profile, used to gate battery- and data-costly behaviour such as hot fuzz.</summary>
public enum PowerProfile
{
    /// <summary>Mains/unmetered (desktop/server): the full cover-traffic set, including hot fuzz, may run.</summary>
    Unmetered,

    /// <summary>Battery/metered (mobile): hot fuzz is suppressed to save power and data.</summary>
    Metered,
}
