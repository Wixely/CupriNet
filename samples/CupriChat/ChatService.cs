using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Arcanum;
using CupriNet.Codex;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Persistence;
using CupriNet.Rites;
using CupriNet.Traversal;

namespace CupriChat;

/// <summary>The network the user picks at startup. Each maps to a fully separate, isolated profile.</summary>
public enum ReachabilityChoice
{
    /// <summary>Direct/LAN/NAT reachability. Fast, not anonymous — your IP is visible to peers.</summary>
    Clearnet,

    /// <summary>Tor only: your IP is hidden, all clearnet paths disabled. Slower to start.</summary>
    Tor,
}

/// <summary>A chat line surfaced to the UI. AuthorId is the sender's Sigil (hex).</summary>
public sealed record ChatMessage(string User, string AuthorId, string Text, DateTimeOffset At, bool IsLocal);

/// <summary>A participant shown in the user list. Name is the raw display name (for @-mentions/completion).</summary>
public sealed record UserView(string Id, string Display, string Name, bool IsSelf, bool IsDirectPeer);

/// <summary>A past channel we can rejoin by re-dialing the trusted peers who proved they knew it.</summary>
public sealed record ChannelHistory(string ChannelName, IReadOnlyList<string> PeerShortIds);

/// <summary>An incoming file offer awaiting the user's decision.</summary>
public sealed record FileOffer(string TransferId, string FromDisplay, string FileName, long Size);

/// <summary>A completed incoming file.</summary>
public sealed record FileReceipt(string FileName, string SavePath);

/// <summary>A request to approve a Ferryman relay: its <paramref name="Fingerprint"/> (compare out-of-band) and the
/// TOFU verdict (<see cref="RelayTrust.New"/> = first use; <see cref="RelayTrust.NameConflict"/> = a changed key).</summary>
public sealed record RelayApprovalRequest(string Fingerprint, RelayTrust Verdict);

/// <summary>
/// The wire form inside an Epistle payload: the author's chosen display name and the message text. The
/// author's cryptographic identity is NOT carried here — it rides the Epistle's authenticated-authorship
/// envelope (see <c>RiteAuthor</c>), which the channel session signs on send and verifies on receive, so
/// authorship survives peer-to-peer relay and cannot be forged by a relaying member. The display name is
/// bound to that identity because it sits inside the signed payload.
/// </summary>
public sealed record ChatWire(string User, string Text)
{
    public static string Serialize(ChatWire wire) => JsonSerializer.Serialize(wire);

    public static ChatWire? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<ChatWire>(json); }
        catch { return null; }
    }
}

/// <summary>Drives a CupriNet node for CupriChat: pairing, channel Consecration, authenticated chat, and file transfers.</summary>
public sealed class ChatService : IAsyncDisposable
{
    private const string NetworkId = "cuprichat";
    private const uint ReliquaryProtocol = 1;
    private const uint HelloProtocol = 2;
    private const uint RosterProtocol = 3;
    private const int MaxRosterEntries = 256;
    private const int MaxNameLength = 64;
    private const int ChunkSize = 64 * 1024;
    private const long MaxFileBytes = 100L * 1024 * 1024; // 100 MB
    private const int MaxPendingOffers = 16;
    private const int MaxConcurrentIncomingPerPeer = 4;
    private const long MaxInFlightBytes = 512L * 1024 * 1024; // 512 MB across all active transfers

    private const uint FlagOffer = 1;
    private const uint FlagAccept = 2;
    private const uint FlagDecline = 3;
    private const uint FlagChunk = 4;

    private readonly object _lock = new();
    private readonly EpistleDeduper _deduper = new();
    private readonly List<PairedPeer> _pending = [];
    private readonly List<PeerSession> _sessions = [];
    private readonly Dictionary<string, string?> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutgoingTransfer> _outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IncomingOffer> _offers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IncomingTransfer> _incoming = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directPersonas = new(StringComparer.Ordinal); // persona ids we hold a direct session with
    private readonly HashSet<string> _connecting = new(StringComparer.Ordinal);     // overlay ids we are dialing (dedup)
    private readonly HashSet<string> _consecrating = new(StringComparer.Ordinal);   // overlay ids mid-Consecration (one session per node)
    private readonly CancellationTokenSource _cts = new();
    private readonly string _scratchDir = Path.Combine(Path.GetTempPath(), "cuprichat", Guid.NewGuid().ToString("N"));

    private long _inFlightBytes;
    private bool _discovering;
    private bool _discoveryAnnounced;
    private CupriNode? _node;
    private ISecretStore? _store;
    private KindredBook? _kindred;
    private KnownRelays _knownRelays = new();
    private Beacon? _relayBeacon;
    private const string KnownRelaysKey = "ferryman/known-relays";

    /// <summary>
    /// Raised when connecting needs a relay this node hasn't already approved. The UI shows a confirm-and-explain
    /// dialog (with the relay's fingerprint) and returns whether to trust and use it; approved relays are remembered.
    /// </summary>
    public Func<RelayApprovalRequest, Task<bool>>? RelayApprovalRequested;
    private RiteIdentity? _persona;
    private IReadOnlyList<Beacon> _selfBeacons = [];
    private string _username = "anon";
    private Watchword _channel = ChannelFromName(DefaultChannelName);
    private string _channelName = DefaultChannelName;
    private string _selfId = string.Empty;
    private bool _joined;

    public const string DefaultChannelName = "CupriChat#Public";

    public event Action<ChatMessage>? MessageArrived;
    public event Action<string>? Status;
    public event Action<IReadOnlyList<UserView>>? UsersChanged;
    public event Action<FileOffer>? FileOfferReceived;
    public event Action<FileReceipt>? FileReceived;

    /// <summary>Chat-log events (joins, leaves, file activity) to be shown inline like a log.</summary>
    public event Action<string>? SystemMessage;

    public bool FileTransfersEnabled { get; set; }

    /// <summary>
    /// Opt-in routed network discovery. Off by default: the app finds channels via the warm cache and the
    /// roster mesh, which never ask the network where a channel is. Turning this on lets the app locate a
    /// channel it has no link/cached peer for, at the cost of leaking (to routing nodes) that it is searching.
    /// </summary>
    public bool NetworkDiscovery { get; set; }

    public string SelfShortId => Short(_selfId);

    public string Username => _username;

    /// <summary>
    /// Supplies the Tor transport for <see cref="ReachabilityChoice.Tor"/>. Left null in builds without Tor
    /// (keeps this sample free of the CupriTor package); an app that references CupriNet.Tor sets it to
    /// <c>(store, ct) =&gt; CupriTorOnionTransport.CreateAsync(store, ct)</c>.
    /// </summary>
    public Func<ISecretStore, CancellationToken, Task<IOnionTransport>>? OnionTransportFactory { get; set; }

    /// <summary>This node's mode for the current run. Chosen at startup and locked for the session.</summary>
    public ReachabilityChoice Mode { get; private set; }

    /// <summary>Fires when the reachability we'd put in a link changes (Tor onion published, NAT-mapped port learned),
    /// so the UI can regenerate the link + QR in place.</summary>
    public event Action? ReachabilityChanged;

    /// <summary>True once we can produce a usable link: clearnet is ready immediately; Tor once the onion is published.</summary>
    public bool ReachabilityReady => Mode == ReachabilityChoice.Clearnet || _node?.OnionBeacon is not null;

    /// <summary>
    /// Infers the mode a pasted link implies: an onion-only link means Tor, a link with any clearnet address means
    /// Clearnet. Returns null if the string isn't a valid link. Used to lock the mode when joining by URL.
    /// </summary>
    public static ReachabilityChoice? DetectMode(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !IntonationUri.TryParse(url.Trim(), out var intonation, out _))
            return null;
        var hasClearnet = intonation.Beacons.Any(b => b.Kind is EndpointKind.Host or EndpointKind.Mapped or EndpointKind.Manual);
        var hasOnion = intonation.Beacons.Any(b => b.Kind == EndpointKind.Onion);
        if (hasOnion && !hasClearnet) return ReachabilityChoice.Tor;
        if (hasClearnet) return ReachabilityChoice.Clearnet;
        return null;
    }

    /// <summary>
    /// A human-readable reason a pasted link can't be joined, or <c>null</c> if it is usable. Distinguishes a
    /// genuinely malformed link from one that is validly signed but carries no reachable address (e.g. a LAN-only
    /// sender whose private address was stripped and who has no public/NAT-mapped route) — the latter used to be
    /// reported, misleadingly, as "not a valid link".
    /// </summary>
    public static string? ExplainUnusable(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Paste a cuprinet:// link to join.";
        if (!IntonationUri.TryParse(url.Trim(), out var intonation, out _))
            return "That doesn't look like a valid cuprinet:// link.";
        var reachable = intonation.Beacons.Any(b =>
            b.Kind is EndpointKind.Host or EndpointKind.Mapped or EndpointKind.Manual or EndpointKind.Onion);
        if (!reachable)
            return "That link is valid but carries no reachable address — the sender is likely on a LAN with no " +
                   "public route (or a NAT that blocks port mapping). Ask them to regenerate it, or connect over Tor.";
        return null;
    }

    /// <summary>
    /// Starts the node in the chosen mode. <paramref name="advertiseAddress"/> (clearnet only) is an optional
    /// <c>host:port</c> — a reachable public address of THIS machine — to put into the link so peers on other
    /// networks can connect. Used to bootstrap/seed a network from a box with a fixed public IP (or a port
    /// forward). Ignored in Tor mode (the .onion is the reachable address there).
    /// </summary>
    public async Task StartAsync(ReachabilityChoice mode = ReachabilityChoice.Clearnet, string? advertiseAddress = null)
    {
        Mode = mode;
        var tor = mode == ReachabilityChoice.Tor;
        var localIp = LocalIPv4();

        // Optional operator-supplied public address to advertise (bootstrapping). Binding the same port locally means
        // a 1:1 public IP or a matching port-forward lines up; we advertise both it and the LAN host so peers on
        // either side of the NAT can pick a beacon that works for them.
        var beacons = new List<Beacon>();
        var listenPort = 0; // 0 = OS-assigned ephemeral (the normal case)
        if (!tor && !string.IsNullOrWhiteSpace(advertiseAddress))
        {
            if (!TryParseHostPort(advertiseAddress!, out var advHost, out var advPort))
                throw new FormatException(
                    $"'{advertiseAddress}' isn't a valid address to advertise. Use host:port, e.g. 203.0.113.5:43820 or seed.example.net:43820.");
            listenPort = advPort;
            beacons.Add(new Beacon(EndpointKind.Manual, advHost, advPort));
            beacons.Add(new Beacon(EndpointKind.Host, localIp, advPort));
        }

        // Optional Ferryman relay (for a home user behind NAT with no port-forward): reserve with it and put a
        // Relay beacon in our link so peers who can't reach us directly can broker a connection. host:port via env.
        _relayBeacon = null;
        var relayEnv = Environment.GetEnvironmentVariable("CUPRICHAT_RELAY");
        if (!tor && !string.IsNullOrWhiteSpace(relayEnv) && TryParseHostPort(relayEnv!, out var relHost, out var relPort))
        {
            _relayBeacon = new Beacon(EndpointKind.Relay, relHost, relPort);
            beacons.Add(_relayBeacon);
        }

        Beacon[]? advertised = beacons.Count > 0 ? beacons.ToArray() : null;

        // Per-MODE profile: clearnet and Tor get entirely separate identities + state under distinct folders, so a
        // Tor identity can never be correlated with a clearnet one (different Sigil, cache, history, keys). CUPRICHAT_HOME
        // still lets two instances on one machine stay distinct.
        var baseHome = Environment.GetEnvironmentVariable("CUPRICHAT_HOME")
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CupriChat");
        var home = Path.Combine(baseHome, tor ? "tor" : "clearnet");
        Directory.CreateDirectory(home);
        var suite = new BouncyCastleSuite();
        var masterKey = KeyFileMasterKey.LoadOrCreate(Path.Combine(home, "master.key"));
        _store = new FileSecretStore(Path.Combine(home, "secrets"), new AeadDataProtector(suite, masterKey));
        _knownRelays = KnownRelays.Decode(await _store.LoadAsync(KnownRelaysKey, _cts.Token).ConfigureAwait(false) ?? []);

        IOnionTransport? onion = null;
        if (tor)
        {
            if (OnionTransportFactory is null)
                throw new InvalidOperationException(
                    "Tor mode isn't available in this build. Reference CupriNet.Tor and set OnionTransportFactory.");
            onion = await OnionTransportFactory(_store, _cts.Token).ConfigureAwait(false);
            onion.Status += s => Status?.Invoke($"Tor {s}"); // surface bootstrap progress, e.g. "Tor [45%] Fetching consensus"
        }

        _node = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = NetworkId,
            ListenAddress = IPAddress.Parse(localIp),
            ListenPort = listenPort,          // fixed only when advertising a manual address, else ephemeral
            AdvertisedBeacons = advertised,   // null = auto-detect the bind address (the normal path)
            Suite = suite,
            SecretStore = _store,
            PersistOverlay = true,            // warm-start: reconnect to known nodes directly, keep gossip fresh
            EnableLanDiscovery = !tor,        // clearnet only (TorOnly enforces this off anyway)
            EnablePortMapping = !tor,
            // A clearnet CupriChat link is handed directly to the one person you want to reach — not gossiped to
            // the overlay — so it must carry this node's local address, otherwise a LAN-only node (no NAT-PMP /
            // public route) produces a beaconless, unusable link. This is a deliberate app opt-in on top of the
            // library default (which strips private/LAN addresses so the overlay never learns your internal IP).
            AdvertiseLocalAddresses = !tor,
            Mode = tor ? ReachabilityMode.TorOnly : ReachabilityMode.Standard,
            OnionTransport = onion,
            // CupriChat opts into cover traffic over Tor: run the connection-fuzz (hot fuzz) and L2-shaped decoy
            // sessions (effigies) over the onion overlay in Tor mode. This is a deliberate app choice on top of the
            // library default (which keeps cover traffic off on Tor to save relay bandwidth); it routes over onion
            // and never touches clearnet.
            AllowCoverTrafficOverTor = tor,
            EnableEffigies = tor,
        }, _cts.Token);
        if (!tor)
            _node.LanPeerDiscovered += OnLanPeerDiscovered;
        _kindred = await KindredBook.LoadAsync(_store, _cts.Token);

        // If a relay is configured, keep a reservation live so peers can reach us through it (we dial the relay
        // as an ordinary address; our link advertised it as a Relay beacon above).
        if (_relayBeacon is not null)
        {
            var relayDial = new Beacon(EndpointKind.Manual, _relayBeacon.Host, _relayBeacon.Port);
            _ = Task.Run(() => _node.MaintainFerrymanReservationAsync(relayDial, _cts.Token));
            Status?.Invoke($"Reachable via relay {_relayBeacon.Host}:{_relayBeacon.Port}.");
        }

        _selfId = Convert.ToHexStringLower(_node.Identity.Sigil.Span);
        // Clearnet advertises its host address (plus any operator-supplied public address); Tor advertises its
        // .onion once the service is published.
        _selfBeacons = tor ? [] : advertised ?? [new Beacon(EndpointKind.Host, localIp, _node.LocalEndPoint.Port)];
        _ = Task.Run(() => WatchReachabilityAsync(_cts.Token));

        lock (_lock)
            _users[_selfId] = _username;
        RaiseUsers();

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Status?.Invoke(tor ? "Starting Tor — this can take a moment…" : $"Ready on {localIp}:{_node.LocalEndPoint.Port}");
    }

    /// <summary>
    /// Watches for the reachability that goes into a link to become available or change — the Tor onion once it's
    /// published, or the externally-reachable NAT-mapped address — and raises <see cref="ReachabilityChanged"/> so
    /// the UI can regenerate the link + QR in place.
    /// </summary>
    private async Task WatchReachabilityAsync(CancellationToken cancellationToken)
    {
        Beacon? lastOnion = null;
        Beacon? lastMapped = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var onion = _node?.OnionBeacon;
            var mapped = _node?.PortMappedBeacon;
            var changed = false;

            if (onion is not null && !onion.Equals(lastOnion))
            {
                lastOnion = onion;
                _selfBeacons = [onion]; // Tor: our reachable identity is the .onion
                Status?.Invoke($"Tor ready — reachable at {onion.Host}");
                changed = true;
            }
            if (mapped is not null && !mapped.Equals(lastMapped))
            {
                lastMapped = mapped; // the router forwarded a port — links can now reach us across networks
                changed = true;
            }

            if (changed)
                ReachabilityChanged?.Invoke();
            try { await Task.Delay(1000, cancellationToken).ConfigureAwait(false); }
            catch { return; }
        }
    }

    /// <summary>Past channels we can rejoin directly, from the trusted-peer book (most recent first).</summary>
    public IReadOnlyList<ChannelHistory> History()
    {
        if (_kindred is null)
            return [];
        return _kindred.Channels()
            .Select(name => new ChannelHistory(name, _kindred.Peers(name).Select(p => Short(Convert.ToHexStringLower(p.Sigil.Span))).ToList()))
            .ToList();
    }

    public string GenerateLink()
    {
        if (_node is null)
            throw new InvalidOperationException("The node is not started yet.");
        var link = _node.IntoneUri(TimeSpan.FromHours(24), DateTimeOffset.UtcNow);
        // Reachability hint so the user knows how well the link will work across networks.
        var mapped = _node.PortMappedBeacon;
        SystemMessage?.Invoke(mapped is not null
            ? $"Directly reachable (router forwarded {mapped.Host}:{mapped.Port}) — this link works across networks."
            : "Not confirmed directly reachable yet. On the same network it just works; across networks, share links both ways.");
        return link;
    }

    public async Task ConnectAsync(string link)
    {
        if (_node is null)
            return;
        if (!IntonationUri.TryParse(link.Trim(), out var intonation, out _))
        {
            Status?.Invoke("That does not look like a CupriChat link.");
            return;
        }

        try
        {
            var peer = await _node.ConjoinAsync(intonation, DateTimeOffset.UtcNow, _cts.Token);
            Status?.Invoke("Paired with a peer.");
            await OnPeerPairedAsync(peer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Direct connection failed. If the link offers a Ferryman relay, broker one (with the user's consent).
            var relay = intonation.Beacons.FirstOrDefault(b => b.Kind == EndpointKind.Relay);
            if (relay is null)
            {
                Status?.Invoke($"Couldn't connect directly: {ex.Message}");
                return;
            }
            await ConnectViaRelayAsync(relay, intonation.InviterSigil);
        }
    }

    /// <summary>Reaches a peer that isn't directly reachable by brokering a hole punch through the relay it advertised.</summary>
    private async Task ConnectViaRelayAsync(Beacon relay, Sigil target)
    {
        if (_node is null)
            return;
        try
        {
            Status?.Invoke("Peer isn't directly reachable — a public relay can broker a direct connection…");
            var relayDial = new Beacon(EndpointKind.Manual, relay.Host, relay.Port);
            var peer = await _node.ConjoinViaFerrymanAsync(relayDial, target, DateTimeOffset.UtcNow, ApproveRelayAsync, _cts.Token);
            Status?.Invoke("Paired via relay.");
            await OnPeerPairedAsync(peer);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status?.Invoke($"Relay connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The relay-trust gate (TOFU): approve silently if we already trust this relay, otherwise ask the UI (which
    /// shows a confirm-and-explain dialog with the relay's fingerprint). Approved relays are remembered to disk.
    /// </summary>
    private async Task<bool> ApproveRelayAsync(Sigil relaySigil)
    {
        var verdict = _knownRelays.Evaluate(relaySigil);
        if (verdict == RelayTrust.Known)
            return true; // already approved — no prompt

        var approved = RelayApprovalRequested is not null
            && await RelayApprovalRequested(new RelayApprovalRequest(RelayFingerprint(relaySigil), verdict)).ConfigureAwait(false);
        if (approved)
        {
            _knownRelays.Approve(relaySigil, null, DateTimeOffset.UtcNow);
            if (_store is not null)
            {
                try { await _store.StoreAsync(KnownRelaysKey, _knownRelays.Encode(), _cts.Token).ConfigureAwait(false); }
                catch { /* best-effort persistence */ }
            }
        }
        return approved;
    }

    /// <summary>A human-comparable fingerprint of a relay's identity (grouped hex), for the trust prompt.</summary>
    private static string RelayFingerprint(Sigil sigil)
    {
        var hex = Convert.ToHexStringLower(sigil.Span);
        return string.Join(" ", Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
    }

    public void SetIdentity(string username, string channelName)
    {
        lock (_lock)
        {
            _username = string.IsNullOrWhiteSpace(username) ? "anon" : username.Trim();
            _channelName = string.IsNullOrWhiteSpace(channelName) ? DefaultChannelName : channelName.Trim();
            _channel = ChannelFromName(_channelName);
            if (_selfId.Length > 0)
                _users[_selfId] = _username;
        }
        RaiseUsers();
    }

    /// <summary>
    /// Rejoins a past channel by re-dialing every trusted peer we cached for it — no fresh link needed.
    /// This is the "route straight back to people we trust" path from the front-page history.
    /// </summary>
    public async Task ReconnectChannelAsync(string channelName, string username)
    {
        if (_node is null || _kindred is null)
            return;
        SetIdentity(username, channelName);
        await EnsurePersonaAsync(channelName);
        lock (_lock)
            _joined = true;

        var peers = _kindred.Peers(channelName);
        if (peers.Count > 0)
        {
            Status?.Invoke($"Reconnecting to {peers.Count} trusted peer(s) in '{channelName}'…");
            foreach (var known in peers)
                _ = Task.Run(() => ReconnectPeerAsync(known, _cts.Token));
        }

        // Even with no cached peers, overlay discovery may find current members over the network.
        EnsureDiscovery();
    }

    private async Task ReconnectPeerAsync(KnownPeer known, CancellationToken cancellationToken)
    {
        try
        {
            var peer = await _node!.ReconnectAsync(known, cancellationToken);
            await ConsecrateAsync(peer, cancellationToken);
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Could not reach a trusted peer ({Short(Convert.ToHexStringLower(known.Sigil.Span))}): {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the OPT-IN routed network discovery loop (idempotent). Off unless <see cref="NetworkDiscovery"/>
    /// is set: the default path relies on the warm cache (trusted-peer reconnect) plus the roster mesh, which
    /// never send a lookup asking the network where a channel is. Routed discovery is the fallback for finding
    /// the first member of a channel you have no link or cached peer for — at the cost of revealing (to the
    /// nodes it routes through) that you are looking for some channel.
    /// </summary>
    private void EnsureDiscovery()
    {
        if (!NetworkDiscovery)
            return;
        lock (_lock)
        {
            if (_discovering)
                return;
            _discovering = true;
        }
        _ = Task.Run(() => RunDiscoveryAsync(_cts.Token));
    }

    /// <summary>
    /// Advertises us as a provider of the current channel and finds other providers over the overlay,
    /// connecting to any we don't already have. Once we've reached one member the roster mesh fans out the
    /// rest, so discovery only needs to find the first — but it keeps running to pick up members who join
    /// later and to refresh our advert before it expires. Requires a bootstrapped Constellation (first
    /// contact is still a link); until then publish/find are no-ops.
    /// </summary>
    private async Task RunDiscoveryAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_node is not null)
                {
                    var now = DateTimeOffset.UtcNow;
                    await _node.PublishChannelAsync(_channel, now, cancellationToken);
                    var providers = await _node.FindChannelProvidersAsync(_channel, now, cancellationToken);

                    var mine = _node.Identity.Sigil;
                    var found = 0;
                    foreach (var decree in providers)
                    {
                        if (decree.ProviderSigil == mine)
                            continue;
                        found++;
                        ConsiderProvider(decree, now);
                    }

                    if (found > 0 && !_discoveryAnnounced)
                    {
                        _discoveryAnnounced = true;
                        SystemMessage?.Invoke($"Found this channel on the network ({found} provider(s)).");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Status?.Invoke($"Discovery: {ex.Message}"); }

            try { await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken); }
            catch { break; }
        }
    }

    /// <summary>Connects to a discovered provider (deduped and mutual-dial-safe via the roster path).</summary>
    private void ConsiderProvider(Decree decree, DateTimeOffset now)
    {
        var known = new KnownPeer
        {
            Sigil = decree.ProviderSigil,
            SealPublicKey = decree.ProviderSealPublicKey,
            Beacons = decree.Endpoints,
            LastSeenUnix = now.ToUnixTimeSeconds(),
        };
        ConsiderRosterMember(known);
    }

    public async Task JoinChannelAsync()
    {
        await EnsurePersonaAsync(_channelName);

        List<PairedPeer> toJoin;
        lock (_lock)
        {
            _joined = true;
            toJoin = [.. _pending];
            _pending.Clear();
        }

        foreach (var peer in toJoin)
            _ = Task.Run(() => ConsecrateAsync(peer, _cts.Token));

        EnsureDiscovery();
    }

    /// <summary>
    /// Loads or creates this channel's persona — a Seal keypair distinct from our overlay identity — and
    /// adopts it as our in-channel identity. We cultivate the overlay under our Sigil, but what we SAY in
    /// this channel is attributed to the persona, unlinkable to that overlay Sigil. Personas are persisted
    /// per channel, so we remain the same member across runs without ever exposing our overlay identity.
    /// </summary>
    private async Task EnsurePersonaAsync(string channelName)
    {
        if (_node is null || _store is null)
            return;

        var key = "persona/" + channelName;
        RiteIdentity persona;
        var stored = await _store.LoadAsync(key, _cts.Token);
        if (stored is not null)
        {
            var r = new CodexReader(stored);
            var priv = r.ReadBytes().ToArray();
            var pub = r.ReadBytes().ToArray();
            persona = new RiteIdentity(pub, priv);
        }
        else
        {
            var seal = _node.Suite.GenerateSeal();
            persona = new RiteIdentity(seal.PublicKey, seal.PrivateKey);
            var w = new CodexWriter();
            w.WriteBytes(seal.PrivateKey);
            w.WriteBytes(seal.PublicKey);
            await _store.StoreAsync(key, w.ToArray(), _cts.Token);
        }

        var personaId = Convert.ToHexStringLower(Sigil.FromSealPublicKey(persona.SealPublicKey).Span);
        lock (_lock)
        {
            _persona = persona;
            _users.Remove(_selfId); // swap our list entry from the overlay id to the channel persona
            _selfId = personaId;
            _users[_selfId] = _username;
        }
        RaiseUsers();
    }

    public async Task SendAsync(string text)
    {
        if (_node is null)
            return;
        // The channel session signs each Epistle with our Seal, so the author identity is authenticated
        // by the rite envelope — we only carry the display name + text here.
        var wire = new ChatWire(_username, text);
        var epistle = Epistle.Text(ChatWire.Serialize(wire), DateTimeOffset.UtcNow);
        lock (_lock)
            _deduper.TryMarkSeen(epistle.MessageId);

        await BroadcastAsync(epistle, except: null, _cts.Token);
        MessageArrived?.Invoke(new ChatMessage(_username, _selfId, text, DateTimeOffset.Now, IsLocal: true));
    }

    // ---- File transfer -------------------------------------------------------------------------

    public async Task SendFileAsync(string personaId, string filePath)
    {
        if (_node is null)
            return;

        // The UI selects a member by persona; transfers are keyed by the overlay (transport) identity.
        var peerSession = PeerSessionFor(personaId);
        if (peerSession is null)
        {
            Status?.Invoke("That user is not directly connected — cannot send a file.");
            return;
        }
        var session = peerSession.Session;
        var peerIdHex = Convert.ToHexStringLower(peerSession.Sigil.Span);

        var info = new FileInfo(filePath);
        if (info.Length > MaxFileBytes)
        {
            Status?.Invoke($"File is too large (max {FormatSize(MaxFileBytes)}).");
            return;
        }

        var content = await File.ReadAllBytesAsync(filePath, _cts.Token);
        var name = Path.GetFileName(filePath);
        var manifest = ReliquaryBuilder.Build([(name, content)], ChunkSize, _node.Suite);
        var transferId = Convert.ToHexStringLower(manifest.TransferId);

        lock (_lock)
            _outgoing[transferId] = new OutgoingTransfer(transferId, manifest, content, session, peerIdHex);

        await session.Conduits.SendAsync(Frame(FlagOffer, ReliquaryCodec.Encode(manifest)), _cts.Token);
        Status?.Invoke($"Offered '{name}' ({FormatSize(content.Length)}). Waiting for the peer to accept…");
    }

    public async Task AcceptFileAsync(string transferId, string savePath)
    {
        IncomingOffer? offer;
        lock (_lock)
            _offers.Remove(transferId, out offer);
        if (offer is null || _node is null)
            return;

        var file = offer.Manifest.Files[0];

        // Ward: refuse if this peer already has too many transfers in flight, or the global in-flight
        // budget would be exceeded. Reserve the declared size up front so concurrent transfers are bounded.
        lock (_lock)
        {
            var perPeer = _incoming.Values.Count(t => t.PeerId == offer.PeerId);
            if (perPeer >= MaxConcurrentIncomingPerPeer || _inFlightBytes + file.Length > MaxInFlightBytes)
            {
                _ = offer.Session.Conduits.SendAsync(Frame(FlagDecline, offer.Manifest.TransferId), _cts.Token);
                Status?.Invoke("Declined a file: too much transfer activity in flight.");
                return;
            }
            _inFlightBytes += file.Length;
        }

        Directory.CreateDirectory(_scratchDir);
        var scratch = Path.Combine(_scratchDir, transferId);
        var assembler = new ReliquaryDiskAssembler(file, _node.Suite, scratch);
        var transfer = new IncomingTransfer(transferId, file, assembler, savePath, offer.PeerId, file.Length);
        lock (_lock)
            _incoming[transferId] = transfer;

        await offer.Session.Conduits.SendAsync(Frame(FlagAccept, offer.Manifest.TransferId), _cts.Token);

        // An empty (zero-chunk) file is already complete and no chunk frames will arrive.
        if (transfer.Assembler.IsComplete)
            await FinalizeIncomingAsync(transfer, _cts.Token);
        else
            Status?.Invoke($"Receiving '{file.RelativePath}'…");
    }

    public async Task DeclineFileAsync(string transferId)
    {
        IncomingOffer? offer;
        lock (_lock)
            _offers.Remove(transferId, out offer);
        if (offer is null)
            return;
        await offer.Session.Conduits.SendAsync(Frame(FlagDecline, offer.Manifest.TransferId), _cts.Token);
    }

    private async Task ConduitLoopAsync(PeerSession peer, CancellationToken cancellationToken)
    {
        var peerId = Convert.ToHexStringLower(peer.Sigil.Span);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await peer.Session.Conduits.ReceiveAsync(cancellationToken);
                if (frame is null)
                    break;

                // Every channel frame is authored by the sender's persona (verified envelope); learn it.
                if (frame.AuthorSealPublicKey is { } personaKey)
                    NotePeerPersona(peer, personaKey);

                if (frame.ProtocolId == HelloProtocol)
                {
                    await HandleHelloAsync(peer, frame.Payload, cancellationToken);
                    continue;
                }
                if (frame.ProtocolId == RosterProtocol)
                {
                    HandlePeerRoster(frame.Payload);
                    continue;
                }
                if (frame.ProtocolId != ReliquaryProtocol)
                    continue;

                switch (frame.Flags)
                {
                    case FlagOffer: await HandleOfferAsync(peer, peerId, frame.Payload, cancellationToken); break;
                    case FlagAccept: await HandleAcceptAsync(peerId, frame.Payload, cancellationToken); break;
                    case FlagDecline: HandleDecline(peerId, frame.Payload); break;
                    case FlagChunk: await HandleChunkAsync(peerId, frame.Payload, cancellationToken); break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status?.Invoke($"File channel closed: {ex.Message}");
        }
        finally
        {
            // Release any incomplete transfers with this peer so their scratch files and in-flight
            // reservations do not leak when it disconnects mid-transfer.
            List<IncomingTransfer> orphaned;
            lock (_lock)
                orphaned = _incoming.Values.Where(t => t.PeerId == peerId).ToList();
            foreach (var transfer in orphaned)
                ReleaseIncoming(transfer);
        }
    }

    /// <summary>Records the peer's channel persona (from its verified frame envelope) and marks it directly connected.</summary>
    private void NotePeerPersona(PeerSession peer, byte[] personaKey)
    {
        if (personaKey.Length is 0 or > 64)
            return;
        var personaHex = Convert.ToHexStringLower(RiteAuthor.AuthorSigil(personaKey).Span);
        bool changed;
        lock (_lock)
        {
            changed = peer.PersonaHex != personaHex;
            peer.PersonaHex = personaHex;
            _directPersonas.Add(personaHex);
            _users.TryAdd(personaHex, null); // show them immediately; the display name resolves via Hello/messages
        }
        if (changed)
            RaiseUsers();
    }

    /// <summary>A greeting sent right after Consecration: our display name and dialable beacons.</summary>
    private async Task SendHelloAsync(PeerSession peer, CancellationToken cancellationToken)
    {
        var w = new CodexWriter();
        w.WriteString(_username.Length > MaxNameLength ? _username[..MaxNameLength] : _username);
        BeaconCodec.Write(w, _selfBeacons, KnownPeerCodec.MaxBeacons);
        var frame = new ConduitFrame { ProtocolId = HelloProtocol, SchemaVersion = 1, Flags = 0, Payload = w.ToArray() };
        try { await peer.Session.Conduits.SendAsync(frame, cancellationToken); }
        catch { /* best-effort */ }
    }

    private async Task HandleHelloAsync(PeerSession peer, byte[] payload, CancellationToken cancellationToken)
    {
        string name;
        IReadOnlyList<Beacon> beacons;
        try
        {
            var r = new CodexReader(payload);
            name = r.ReadString();
            beacons = BeaconCodec.Read(ref r, KnownPeerCodec.MaxBeacons);
        }
        catch { return; }
        if (name.Length > MaxNameLength)
            name = name[..MaxNameLength];

        if (peer.PersonaHex is { } persona)
        {
            UpdateUser(persona, name);
            if (!peer.JoinAnnounced)
            {
                peer.JoinAnnounced = true;
                SystemMessage?.Invoke($"{name}#{Short(persona)} joined the channel.");
            }
        }

        // The peer Consecrated with us, so it proved it knew this channel. Remember its overlay dial-info
        // under the channel name so we (and our roster) can route straight back to it later.
        if (_kindred is not null && peer.SealPublicKey.Length > 0 && beacons.Count > 0)
        {
            var known = new KnownPeer
            {
                Sigil = peer.Sigil,
                SealPublicKey = peer.SealPublicKey,
                Beacons = beacons,
                LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            string channelName;
            lock (_lock)
                channelName = _channelName;
            try { await _kindred.RememberAsync(channelName, known, cancellationToken); }
            catch (Exception ex) { Status?.Invoke($"Could not remember a trusted peer: {ex.Message}"); }
        }
    }

    /// <summary>Sends the members we know (their dialable overlay info) so the peer can join the full mesh.</summary>
    private async Task SendRosterAsync(PeerSession peer, CancellationToken cancellationToken)
    {
        if (_node is null)
            return;

        List<KnownPeer> roster =
        [
            new KnownPeer
            {
                Sigil = _node.Identity.Sigil,
                SealPublicKey = _node.Identity.Seal.PublicKey,
                Beacons = _selfBeacons,
                LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            },
        ];
        lock (_lock)
        {
            if (_kindred is not null)
                roster.AddRange(_kindred.Peers(_channelName));
        }
        if (roster.Count > MaxRosterEntries)
            roster = roster.Take(MaxRosterEntries).ToList();

        var w = new CodexWriter();
        w.WriteVarUInt((ulong)roster.Count);
        foreach (var member in roster)
            w.WriteBytes(KnownPeerCodec.Encode(member));
        var frame = new ConduitFrame { ProtocolId = RosterProtocol, SchemaVersion = 1, Flags = 0, Payload = w.ToArray() };
        try { await peer.Session.Conduits.SendAsync(frame, cancellationToken); }
        catch { /* best-effort */ }
    }

    private void HandlePeerRoster(byte[] payload)
    {
        var r = new CodexReader(payload);
        ulong count;
        try { count = r.ReadVarUInt(); }
        catch { return; }
        if (count > (ulong)MaxRosterEntries)
            return;

        for (var i = 0UL; i < count; i++)
        {
            KnownPeer member;
            try { member = KnownPeerCodec.Decode(r.ReadBytes()); }
            catch { break; }
            ConsiderRosterMember(member);
        }
    }

    /// <summary>Dials a rostered member we are not already connected to, so a single join fans out to the whole mesh.</summary>
    /// <summary>
    /// A peer on the same local network announced itself. If we're in a channel, try to pair with it directly (no
    /// link needed) and Consecrate — only same-channel peers will complete the handshake, so this quietly finds
    /// the people in your channel who are on your network.
    /// </summary>
    private void OnLanPeerDiscovered(DiscoveredNode node)
    {
        bool joined;
        lock (_lock)
            joined = _joined;
        if (!joined)
            return;

        var known = new KnownPeer
        {
            Sigil = node.Sigil,
            SealPublicKey = node.SealPublicKey,
            Beacons = [node.ToBeacon()],
            LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        SystemMessage?.Invoke($"Found a peer on your network ({Short(Convert.ToHexStringLower(node.Sigil.Span))}); trying to connect…");
        ConsiderRosterMember(known); // reuses the lower-Sigil-dials rule, dedup, dial + Consecrate
    }

    private void ConsiderRosterMember(KnownPeer known)
    {
        if (_node is null)
            return;
        // To avoid a simultaneous mutual dial (both nodes dialing each other from a common introducer),
        // only the lower-Sigil node dials; the higher-Sigil node waits to accept. This yields exactly one
        // connection per pair, deterministically.
        if (_node.Identity.Sigil.Span.SequenceCompareTo(known.Sigil.Span) >= 0)
            return;

        var sigilHex = Convert.ToHexStringLower(known.Sigil.Span);
        lock (_lock)
        {
            if (sigilHex == Convert.ToHexStringLower(_node.Identity.Sigil.Span))
                return; // it's us
            if (_sessions.Any(s => Convert.ToHexStringLower(s.Sigil.Span) == sigilHex))
                return; // already connected
            if (!_connecting.Add(sigilHex))
                return; // already dialing
        }

        _ = Task.Run(async () =>
        {
            try { await ReconnectPeerAsync(known, _cts.Token); }
            finally
            {
                lock (_lock)
                    _connecting.Remove(sigilHex);
            }
        });
    }

    private async Task HandleOfferAsync(PeerSession peer, string peerId, byte[] payload, CancellationToken cancellationToken)
    {
        ReliquaryManifest manifest;
        try { manifest = ReliquaryCodec.Decode(payload); }
        catch { return; }
        if (manifest.Files.Count == 0)
            return;

        var transferId = Convert.ToHexStringLower(manifest.TransferId);
        var file = manifest.Files[0];

        if (!FileTransfersEnabled || file.Length > MaxFileBytes)
        {
            await peer.Session.Conduits.SendAsync(Frame(FlagDecline, manifest.TransferId), cancellationToken);
            return;
        }

        string fromName;
        lock (_lock)
        {
            if (_offers.Count >= MaxPendingOffers)
            {
                // too many pending offers from a flood — refuse without storing
                _ = peer.Session.Conduits.SendAsync(Frame(FlagDecline, manifest.TransferId), cancellationToken);
                return;
            }
            _offers[transferId] = new IncomingOffer(transferId, manifest, peer.Session, peerId);
            var displayId = peer.PersonaHex ?? peerId;
            fromName = _users.TryGetValue(displayId, out var n) && n is not null ? n : "(peer)";
        }

        var fromId = peer.PersonaHex ?? peerId;
        FileOfferReceived?.Invoke(new FileOffer(transferId, $"{fromName}#{Short(fromId)}", Path.GetFileName(file.RelativePath), file.Length));
    }

    private async Task HandleAcceptAsync(string peerId, byte[] transferIdBytes, CancellationToken cancellationToken)
    {
        var transferId = Convert.ToHexStringLower(transferIdBytes);
        OutgoingTransfer? transfer;
        lock (_lock)
            _outgoing.TryGetValue(transferId, out transfer);
        if (transfer is null || transfer.PeerId != peerId) // only the offered peer may accept
            return;

        var file = transfer.Manifest.Files[0];
        for (var i = 0; i < file.ChunkCount; i++)
        {
            var start = i * file.ChunkSize;
            var length = Math.Min(file.ChunkSize, transfer.Content.Length - start);
            var w = new CodexWriter();
            w.WriteBytes(transfer.Manifest.TransferId);
            w.WriteVarUInt((ulong)i);
            w.WriteBytes(transfer.Content.AsSpan(start, length));
            await transfer.Session.Conduits.SendAsync(Frame(FlagChunk, w.ToArray()), cancellationToken);
        }

        lock (_lock)
            _outgoing.Remove(transferId);
        SystemMessage?.Invoke($"Sent file '{file.RelativePath}'.");
    }

    private void HandleDecline(string peerId, byte[] transferIdBytes)
    {
        var transferId = Convert.ToHexStringLower(transferIdBytes);
        lock (_lock)
        {
            if (_outgoing.TryGetValue(transferId, out var t) && t.PeerId == peerId)
                _outgoing.Remove(transferId);
            else
                return;
        }
        SystemMessage?.Invoke("The peer declined the file.");
    }

    private async Task HandleChunkAsync(string peerId, byte[] payload, CancellationToken cancellationToken)
    {
        var reader = new CodexReader(payload);
        var transferId = Convert.ToHexStringLower(reader.ReadBytes());
        var chunkIndex = (int)reader.ReadVarUInt();
        var data = reader.ReadBytes();

        IncomingTransfer? transfer;
        lock (_lock)
            _incoming.TryGetValue(transferId, out transfer);
        if (transfer is null || transfer.PeerId != peerId) // only the offering peer may deliver chunks
            return;

        if (!transfer.Assembler.AcceptChunk(chunkIndex, data) || !transfer.Assembler.IsComplete)
            return;

        await FinalizeIncomingAsync(transfer, cancellationToken);
    }

    private Task FinalizeIncomingAsync(IncomingTransfer transfer, CancellationToken cancellationToken)
    {
        try
        {
            // Streams the scratch file through a whole-file hash check, then atomically moves it into place.
            transfer.Assembler.CompleteTo(transfer.SavePath);
            FileReceived?.Invoke(new FileReceipt(Path.GetFileName(transfer.SavePath), transfer.SavePath));
        }
        catch (Exception ex)
        {
            Status?.Invoke($"File failed verification: {ex.Message}");
        }
        finally
        {
            ReleaseIncoming(transfer);
        }

        return Task.CompletedTask;
    }

    /// <summary>Removes a transfer, disposes its disk assembler (deleting any scratch), and frees its in-flight reservation.</summary>
    private void ReleaseIncoming(IncomingTransfer transfer)
    {
        lock (_lock)
        {
            if (_incoming.Remove(transfer.Id))
                _inFlightBytes -= transfer.ReservedBytes;
        }
        transfer.Assembler.Dispose();
    }

    // ---- Pairing / channel ---------------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_node is null)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var peer = await _node.AcceptAsync(cancellationToken);
                Status?.Invoke("A peer paired with us.");
                // Do not block the accept loop on Consecration (which can wait for the peer to join).
                _ = Task.Run(() => OnPeerPairedAsync(peer));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Status?.Invoke($"Connection dropped: {ex.Message}");
            }
        }
    }

    private async Task OnPeerPairedAsync(PairedPeer peer)
    {
        // Reject a self-pairing outright (e.g. pasting our own link) before it reaches the channel.
        if (_node is not null && peer.PeerSigil == _node.Identity.Sigil)
        {
            Status?.Invoke("Ignored a connection to ourselves.");
            await peer.DisposeAsync();
            return;
        }

        bool joined;
        lock (_lock)
        {
            joined = _joined;
            if (!joined)
                _pending.Add(peer);
        }

        if (joined)
            await ConsecrateAsync(peer, _cts.Token);
    }

    private async Task ConsecrateAsync(PairedPeer peer, CancellationToken cancellationToken)
    {
        if (_node is null)
        {
            await peer.DisposeAsync();
            return;
        }

        // Never establish a channel with ourselves, and never hold more than one session with the same
        // node (identified by its overlay Sigil, regardless of which of its IPs we reached).
        var sigilHex = Convert.ToHexStringLower(peer.PeerSigil.Span);
        bool reserved;
        lock (_lock)
        {
            reserved = peer.PeerSigil != _node.Identity.Sigil
                       && !_sessions.Any(s => s.Sigil == peer.PeerSigil)
                       && _consecrating.Add(sigilHex);
        }
        if (!reserved)
        {
            await peer.DisposeAsync();
            return;
        }

        try
        {
            var options = new ConsecrateOptions { ChannelIdentity = _persona };
            var session = await _node.ConsecrateAsync(peer, _channel, DateTimeOffset.UtcNow, options, cancellationToken);
            var peerSession = new PeerSession(peer.PeerSigil, peer.PeerSealPublicKey, session);

            lock (_lock)
                _sessions.Add(peerSession);
            RaiseUsers();
            // The named "joined" line is logged when the peer's Hello arrives (see HandleHelloAsync).
            _ = Task.Run(() => ReceiveLoopAsync(peerSession, cancellationToken));
            _ = Task.Run(() => ConduitLoopAsync(peerSession, cancellationToken));

            // Greet the peer (name + dialable beacons), then share the members we know so a single join
            // fans out into a direct mesh with everyone in the channel.
            await SendHelloAsync(peerSession, cancellationToken);
            await SendRosterAsync(peerSession, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await peer.DisposeAsync();
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Could not join channel with a peer: {ex.Message}");
            await peer.DisposeAsync();
        }
        finally
        {
            lock (_lock)
                _consecrating.Remove(sigilHex);
        }
    }

    private async Task ReceiveLoopAsync(PeerSession peer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await peer.Session.Epistles.ReceiveAsync(cancellationToken);
                if (received is null)
                    break;
                if (received is not MessageReceived message)
                    continue;

                bool isNew;
                lock (_lock)
                    isNew = _deduper.TryMarkSeen(message.Epistle.MessageId);
                if (!isNew)
                    continue;

                // The channel session already verified the author envelope (RequireSignedAuthors), so the
                // author's Seal key is present and authentic. The identity comes from that envelope — never
                // from any self-declared field — and survives relay because we forward the Epistle verbatim.
                var authorKey = message.Epistle.AuthorSealPublicKey;
                if (authorKey is null)
                    continue;
                var authorId = Convert.ToHexStringLower(RiteAuthor.AuthorSigil(authorKey).Span);
                if (authorId == _selfId)
                    continue; // ignore our own relayed messages

                var wire = ChatWire.Deserialize(message.Epistle.AsText());
                if (wire is null)
                    continue;

                UpdateUser(authorId, wire.User);
                MessageArrived?.Invoke(new ChatMessage(wire.User, authorId, wire.Text, DateTimeOffset.Now, IsLocal: false));
                await BroadcastAsync(message.Epistle, except: peer.Session, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status?.Invoke($"A peer disconnected: {ex.Message}");
        }
        finally
        {
            string? leftLabel = null;
            lock (_lock)
            {
                _sessions.RemoveAll(p => ReferenceEquals(p.Session, peer.Session));
                if (peer.PersonaHex is { } persona)
                {
                    _directPersonas.Remove(persona);
                    // If no remaining direct session holds this persona, the member has left our view —
                    // drop it from the user list so it no longer lingers in the toolbar.
                    if (!_sessions.Any(s => s.PersonaHex == persona))
                    {
                        _users.Remove(persona, out var name);
                        leftLabel = $"{(string.IsNullOrEmpty(name) ? "(peer)" : name)}#{Short(persona)}";
                    }
                }
            }
            RaiseUsers();
            if (leftLabel is not null)
                SystemMessage?.Invoke($"{leftLabel} left the channel.");
            await peer.Session.DisposeAsync();
        }
    }

    private async Task BroadcastAsync(Epistle epistle, ArcanumSession? except, CancellationToken cancellationToken)
    {
        List<ArcanumSession> targets;
        lock (_lock)
            targets = _sessions.Where(p => !ReferenceEquals(p.Session, except)).Select(p => p.Session).ToList();

        foreach (var session in targets)
        {
            try { await session.Epistles.SendMessageAsync(epistle, cancellationToken); }
            catch { }
        }
    }

    /// <summary>Finds a direct session by the member's channel persona id (or, as a fallback, its overlay id).</summary>
    private PeerSession? PeerSessionFor(string id)
    {
        lock (_lock)
            return _sessions.FirstOrDefault(p => p.PersonaHex == id)
                   ?? _sessions.FirstOrDefault(p => Convert.ToHexStringLower(p.Sigil.Span) == id);
    }

    private void UpdateUser(string id, string name)
    {
        lock (_lock)
            _users[id] = name;
        RaiseUsers();
    }

    private void RaiseUsers()
    {
        List<UserView> snapshot;
        lock (_lock)
        {
            // The user list is keyed by channel PERSONA (the in-channel identity), never the overlay Sigil.
            snapshot = _users
                .Select(kv => new UserView(kv.Key, FormatUser(kv.Key, kv.Value), kv.Value ?? string.Empty, kv.Key == _selfId, _directPersonas.Contains(kv.Key)))
                .OrderByDescending(u => u.IsSelf)
                .ThenBy(u => u.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        UsersChanged?.Invoke(snapshot);
    }

    private string FormatUser(string id, string? name)
    {
        var display = $"{name ?? "(joining…)"}#{Short(id)}";
        return id == _selfId ? $"{display} (you)" : display;
    }

    private static ConduitFrame Frame(uint flags, byte[] payload)
        => new() { ProtocolId = ReliquaryProtocol, SchemaVersion = 1, Flags = flags, Payload = payload };

    private static string Short(string idHex) => idHex.Length >= 6 ? idHex[..6] : idHex;

    private static string FormatSize(long bytes)
        => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

    private static Watchword ChannelFromName(string name)
    {
        var seed = Encoding.UTF8.GetBytes("cuprichat/channel/" + name.ToLowerInvariant());
        var salt = SHA256.HashData(seed).AsSpan(0, 16).ToArray();
        var code = $"channel#{Base64Url.EncodeToString(salt)}";
        if (!Watchword.TryParse(code, out var watchword))
            throw new InvalidOperationException("Failed to derive the channel watchword.");
        return watchword;
    }

    private static string LocalIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    /// <summary>
    /// Parses <c>host:port</c> where host is an IPv4/IPv6/DNS name. IPv6 must be bracketed (<c>[::1]:43820</c>).
    /// Accepts a DNS name or literal IP; the port must be 1–65535.
    /// </summary>
    internal static bool TryParseHostPort(string text, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        text = text.Trim();
        if (text.Length == 0)
            return false;

        string portPart;
        if (text.StartsWith('['))                       // bracketed IPv6: [addr]:port
        {
            var end = text.IndexOf(']');
            if (end <= 1 || end + 1 >= text.Length || text[end + 1] != ':')
                return false;
            host = text[1..end];
            portPart = text[(end + 2)..];
            if (!IPAddress.TryParse(host, out _))       // must be a real IPv6 literal inside brackets
                return false;
        }
        else
        {
            var idx = text.LastIndexOf(':');            // host:port (rightmost colon splits the port)
            if (idx <= 0 || idx == text.Length - 1)
                return false;
            host = text[..idx];
            portPart = text[(idx + 1)..];
            if (host.Contains(':'))                      // an unbracketed IPv6 is ambiguous — reject it
                return false;
        }

        return int.TryParse(portPart, out port) && port is > 0 and <= 65535 && host.Length > 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        List<PeerSession> sessions;
        List<PairedPeer> pending;
        lock (_lock)
        {
            sessions = [.. _sessions];
            pending = [.. _pending];
            _pending.Clear();
        }
        foreach (var peer in sessions)
            await peer.Session.DisposeAsync();
        foreach (var peer in pending)
            await peer.DisposeAsync();

        List<IncomingTransfer> incoming;
        lock (_lock)
        {
            incoming = [.. _incoming.Values];
            _incoming.Clear();
            _inFlightBytes = 0;
        }
        foreach (var transfer in incoming)
            transfer.Assembler.Dispose(); // deletes any scratch file for an unfinished transfer

        if (_node is not null)
            await _node.DisposeAsync();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); } catch { /* best-effort */ }
        _cts.Dispose();
    }

    private sealed record PeerSession(Sigil Sigil, byte[] SealPublicKey, ArcanumSession Session)
    {
        /// <summary>The peer's channel persona (its authenticated in-channel identity), learned from its frames.</summary>
        public string? PersonaHex { get; set; }

        /// <summary>Whether we've already logged this peer's "joined" line (once per session).</summary>
        public bool JoinAnnounced { get; set; }
    }

    private sealed record OutgoingTransfer(string Id, ReliquaryManifest Manifest, byte[] Content, ArcanumSession Session, string PeerId);

    private sealed record IncomingOffer(string Id, ReliquaryManifest Manifest, ArcanumSession Session, string PeerId);

    private sealed record IncomingTransfer(string Id, ReliquaryFile File, ReliquaryDiskAssembler Assembler, string SavePath, string PeerId, long ReservedBytes);
}
