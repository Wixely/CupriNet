namespace CupriNet.Lodestar;

/// <summary>
/// Configuration for a Lodestar node. Bound from the <c>Lodestar</c> section of appsettings.json, then
/// overridden by environment variables (prefix <c>CUPRINET_LODESTAR_</c>) and command-line arguments.
/// </summary>
public sealed class LodestarOptions
{
    /// <summary>Config section these options bind from.</summary>
    public const string SectionName = "Lodestar";

    /// <summary>The network (Concordance) this node keeps alive. Required — a node must know which network it serves.</summary>
    public string Concordium { get; set; } = "";

    /// <summary>Address to bind the listener to. Defaults to all interfaces (a server is meant to be reachable).</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>TCP port to listen on. 0 = an OS-assigned ephemeral port (not useful for a stable seed; set it).</summary>
    public int ListenPort { get; set; } = 43820;

    /// <summary>
    /// Durable "hot path" directory: the node's identity, encryption master key, and the cache of known peers
    /// (the keys of nodes it has met) live here and are re-read on every startup. If empty, a sensible per-OS
    /// default is chosen (see <c>LodestarService</c>).
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// Public host (DNS name or IP) to advertise in this node's own link, so peers can actually reach it from
    /// off-box. Set this on a server with a stable address; without it the link only advertises the bind address.
    /// </summary>
    public string? PublicHost { get; set; }

    /// <summary>Public port to advertise alongside <see cref="PublicHost"/>. Defaults to <see cref="ListenPort"/>.</summary>
    public int? PublicPort { get; set; }

    /// <summary>
    /// Optional self-asserted display name (Moniker) this node advertises — e.g. "Wikipedia". It rides in the node's
    /// signed link and peer record, so it can only be claimed for this node's own key. CupriNet <b>never verifies</b>
    /// it: peers see it as a hint and decide whether to trust it, always by matching this node's fingerprint. Leave
    /// empty to advertise no name.
    /// </summary>
    public string? Moniker { get; set; }

    /// <summary>
    /// Seed links (<c>cuprinet://intone/…</c>) to bootstrap from on first start. A seed carries another node's
    /// key + reachability. As many as you like — every reachable one grows the local map. Also gathered from
    /// <see cref="SeedsFile"/>, the <c>CUPRINET_LODESTAR_SEEDS</c> env var, and repeated <c>--seed</c> arguments.
    /// </summary>
    public List<string> SeedLinks { get; set; } = new();

    /// <summary>Optional path to a file of seed links, one per line (blank lines and <c>#</c> comments ignored).</summary>
    public string? SeedsFile { get; set; }

    /// <summary>
    /// Extra reachable addresses to advertise in this node's link, for a bootstrap situation where the service
    /// has public IPs it can't discover itself (a cloud NAT/load balancer, a second interface, a DNS name). Each
    /// entry is <c>host</c> or <c>host:port</c> (bracket IPv6, e.g. <c>[2001:db8::1]:43820</c>); a bare host uses
    /// <see cref="PublicPort"/> or <see cref="ListenPort"/>. These are added alongside <see cref="PublicHost"/>.
    /// </summary>
    public List<string> AdvertisedAddresses { get; set; } = new();

    /// <summary>
    /// Serve a small read-only HTTP status page (HTTP only — front it with a reverse proxy for TLS) showing this
    /// node's current connection link and a QR code, auto-refreshing in the browser. Off by default.
    /// </summary>
    public bool EnableWeb { get; set; }

    /// <summary>Interface the status page binds to. <c>0.0.0.0</c>/<c>any</c> = all interfaces.</summary>
    public string WebListenAddress { get; set; } = "0.0.0.0";

    /// <summary>TCP port for the status page.</summary>
    public int WebPort { get; set; } = 8080;

    /// <summary>
    /// How often (seconds) the link is regenerated and the browser re-polls. The link is cached between
    /// regenerations, so it is not minted on every request. Minimum 5.
    /// </summary>
    public int WebRefreshSeconds { get; set; } = 30;

    /// <summary>
    /// When the node is dual-stack (<see cref="EnableTor"/> + <see cref="EnableWeb"/>), split the status page by
    /// transport: the clearnet page shows a <em>clearnet-only</em> link, and the page is also published as its own
    /// <c>.onion</c> that shows a <em>Tor-only</em> link — so a visitor reaching it over Tor is never handed the
    /// node's clearnet IP. Effectively two links from one node, one per transport. On by default (a safety default:
    /// prevents accidental deanonymisation). No effect without Tor + the web page.
    /// </summary>
    public bool WebSplit { get; set; } = true;

    /// <summary>
    /// Subnets this node may connect to and accept from — CIDR (<c>10.0.0.0/8</c>), a netmask (<c>10.0.0.0/255.0.0.0</c>),
    /// or a bare IP. An allow-list match always beats the deny-list. Empty = LAN + WAN (no fence). For a private
    /// CupriNet or LAN-only node, set <see cref="DeniedSubnets"/> to <c>0.0.0.0/0</c> and list your subnets here.
    /// Not applied over Tor.
    /// </summary>
    public List<string> AllowedSubnets { get; set; } = new();

    /// <summary>Subnets this node refuses (unless also in <see cref="AllowedSubnets"/>, which wins).</summary>
    public List<string> DeniedSubnets { get; set; } = new();

    /// <summary>
    /// Also run Tor <em>alongside</em> clearnet (dual-stack): publish a v3 <c>.onion</c> and accept/dial onion peers
    /// while still advertising a clearnet address. The link then carries both, so the node is reachable by clearnet
    /// peers <em>and</em> by Tor peers. Off by default. The <c>.onion</c> appears once Tor finishes bootstrapping.
    /// For a public keep-alive node this is usually the right choice — see <see cref="TorOnly"/> to hide the IP.
    /// </summary>
    public bool EnableTor { get; set; }

    /// <summary>
    /// Run <em>onion-only</em> (implies <see cref="EnableTor"/>): reach peers solely through Tor and never touch
    /// clearnet, hiding this node's IP. Clearnet settings (<see cref="PublicHost"/>, <see cref="AdvertisedAddresses"/>,
    /// <see cref="EnableLanDiscovery"/>, <see cref="EnablePortMapping"/>) do not apply. This restricts who can reach
    /// the node to Tor-capable peers, so prefer dual-stack (<see cref="EnableTor"/> alone) unless anonymity is the goal.
    /// </summary>
    public bool TorOnly { get; set; }

    /// <summary>
    /// Act as a <b>Ferryman</b> — a public relay that brokers hole punches between NAT'd peers (signaling only,
    /// never channel content). On by default: a reachable keep-alive node is the ideal relay, and it helps home
    /// users behind NAT reach each other. No effect in onion-only mode (there is no clearnet path to broker).
    /// </summary>
    public bool EnableFerryman { get; set; } = true;

    /// <summary>Ask the home gateway (NAT-PMP) to forward the listen port. Off by default — servers usually have a public IP.</summary>
    public bool EnablePortMapping { get; set; }

    /// <summary>Announce/discover peers on the local LAN. Off by default (a broadcast); handy for an all-LAN cluster.</summary>
    public bool EnableLanDiscovery { get; set; }

    /// <summary>
    /// Run anonymity cover traffic (hot fuzz decoy connections, acting as a Pageant member). Off by default: a
    /// Lodestar's job is liveness + routing, and cover traffic is bandwidth an infrastructure node rarely needs.
    /// </summary>
    public bool EnableCoverTraffic { get; set; }

    /// <summary>Seconds between overlay gossip rounds (keeps the local map fresh and re-checks known peers).</summary>
    public int GossipIntervalSeconds { get; set; } = 60;

    /// <summary>How many known nodes each gossip round contacts.</summary>
    public int GossipFanout { get; set; } = 3;

    /// <summary>How many seed links to dial concurrently during bootstrap.</summary>
    public int SeedConnectConcurrency { get; set; } = 8;

    /// <summary>Write this node's own connection link to <c>&lt;DataDirectory&gt;/lodestar.link</c> on startup.</summary>
    public bool WriteSelfLink { get; set; } = true;

    /// <summary>Lifetime (hours) stamped into this node's own link when it is minted at startup.</summary>
    public int SelfLinkLifetimeHours { get; set; } = 24;
}
