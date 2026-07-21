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

    /// <summary>Max inbound overlay-control connections served concurrently across all peers — a Ward against connection floods.</summary>
    public int MaxConcurrentControlConnections { get; init; } = 256;

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
    /// Require a pre-handshake stateless cookie (the "Toll") before the Noise transport handshake. The
    /// responder keeps no per-connection state to validate it, so an attacker cannot exhaust responder
    /// memory by opening connections and stalling before the expensive crypto. Both peers must agree;
    /// default true. Disable only for interop with peers that predate the Toll.
    /// </summary>
    public bool EnableToll { get; init; } = true;
}

/// <summary>Power/connectivity profile, used to gate battery- and data-costly behaviour such as hot fuzz.</summary>
public enum PowerProfile
{
    /// <summary>Mains/unmetered (desktop/server): the full cover-traffic set, including hot fuzz, may run.</summary>
    Unmetered,

    /// <summary>Battery/metered (mobile): hot fuzz is suppressed to save power and data.</summary>
    Metered,
}
