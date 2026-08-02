using System.Diagnostics;
using System.Net;
using CupriNet.Abstractions;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Persistence;
using CupriNet.Tor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CupriNet.Lodestar;

/// <summary>
/// The Lodestar worker: brings up a headless CupriNet node that runs only the Layer-1 Concordance overlay —
/// gossiping, routing discovery/referrals, and carrying channel <em>advertisements</em> (metadata about L2),
/// but never holding a channel session itself (no L2 content). It boots from seed links, persists the keys of
/// nodes it meets to a durable "hot path" so restarts are warm, and can stand up a brand-new network on its own
/// (genesis), emitting a link others can seed from.
/// </summary>
public sealed class LodestarService : BackgroundService
{
    private readonly LodestarOptions _options;
    private readonly ILogger<LodestarService> _log;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string[] _commandLineArgs;

    // The concrete onion transport (when Tor is enabled), kept so the status page can be published as its own onion.
    private CupriTorOnionTransport? _onion;

    public LodestarService(
        IOptions<LodestarOptions> options,
        ILogger<LodestarService> log,
        IHostApplicationLifetime lifetime)
    {
        _options = options.Value;
        _log = log;
        _lifetime = lifetime;
        _commandLineArgs = Environment.GetCommandLineArgs();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Concordium))
        {
            _log.LogCritical(
                "No network configured. Set 'Lodestar:Concordium' (appsettings.json), the " +
                "CUPRINET_LODESTAR_Concordium environment variable, or pass it on the command line.");
            _lifetime.StopApplication();
            return;
        }

        CupriNode node;
        string dataDir = ResolveDataDirectory();
        try
        {
            node = await StartNodeAsync(dataDir, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Lodestar failed to start.");
            _lifetime.StopApplication();
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            AnnounceSelf(node, dataDir, stoppingToken);
            await BootstrapFromSeedsAsync(node, stoppingToken).ConfigureAwait(false);

            // Serve inbound overlay control forever, and keep the map fresh / persisted, until shutdown.
            var loops = new List<Task>
            {
                AcceptLoopAsync(node, stoppingToken),
                MaintenanceLoopAsync(node, startedAt, stoppingToken),
            };
            if (_options.EnableWeb)
                loops.Add(WebLoopAsync(node, dataDir, stoppingToken));
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Lodestar stopped on an unexpected error.");
        }
        finally
        {
            try { await node.SaveOverlayStateAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "Final overlay save failed."); }
            await node.DisposeAsync().ConfigureAwait(false);
            _log.LogInformation("Lodestar stopped.");
        }
    }

    private async Task<CupriNode> StartNodeAsync(string dataDir, CancellationToken ct)
    {
        Directory.CreateDirectory(dataDir);
        _log.LogInformation("Data directory (hot path): {Dir}", dataDir);

        // Durable, encrypted store: identity + guard state + the cache of known peers all survive restarts.
        var suite = new BouncyCastleSuite();
        var masterKey = KeyFileMasterKey.LoadOrCreate(Path.Combine(dataDir, "master.key"));
        var store = new FileSecretStore(Path.Combine(dataDir, "secrets"), new AeadDataProtector(suite, masterKey));

        var onionOnly = _options.TorOnly;
        var torEnabled = _options.EnableTor || onionOnly;   // onion-only implies Tor

        CupriTorOnionTransport? onion = null;
        if (torEnabled)
        {
            _log.LogInformation(onionOnly
                ? "Tor (onion-only): building the onion transport (managed Tor client)…"
                : "Tor (dual-stack: clearnet + onion): building the onion transport (managed Tor client)…");
            onion = await CupriTorOnionTransport.CreateAsync(store, ct).ConfigureAwait(false);
            onion.Status += s => _log.LogInformation("Tor {Status}", s); // bootstrap/connect progress, e.g. "Tor [45%] …"
            if (onionOnly && (!string.IsNullOrWhiteSpace(_options.PublicHost) || _options.AdvertisedAddresses.Count > 0))
                _log.LogInformation("PublicHost/AdvertisedAddresses are ignored in onion-only mode — the link advertises the .onion only.");
        }
        _onion = onion; // kept so WebLoopAsync can publish the status page as its own onion

        // Clearnet reachability is dropped only in onion-only mode; dual-stack advertises it alongside the onion.
        var advertised = onionOnly ? null : BuildAdvertisedBeacons();

        var node = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = _options.Concordium,
            ListenAddress = ParseAddress(_options.ListenAddress),
            ListenPort = _options.ListenPort,
            Suite = suite,
            SecretStore = store,
            AdvertisedBeacons = advertised,
            Moniker = _options.Moniker,   // self-asserted display name, carried unverified in link + peer record
            OnionTransport = onion,
            // Standard + an OnionTransport = dual-stack (clearnet AND onion); TorOnly enforces onion-only.
            Mode = onionOnly ? ReachabilityMode.TorOnly : ReachabilityMode.Standard,
            PersistOverlay = true,               // warm start: reload known peers' keys from the hot path
            EnableOverlayGossip = true,          // keep the map fresh and re-check known peers
            OverlayGossipIntervalSeconds = _options.GossipIntervalSeconds,
            OverlayGossipFanout = _options.GossipFanout,
            EnableLanDiscovery = !onionOnly && _options.EnableLanDiscovery,
            EnablePortMapping = !onionOnly && _options.EnablePortMapping,
            AllowedSubnets = _options.AllowedSubnets.Count > 0 ? _options.AllowedSubnets : null,
            DeniedSubnets = _options.DeniedSubnets.Count > 0 ? _options.DeniedSubnets : null,
            EnableHotFuzz = _options.EnableCoverTraffic,   // cover traffic is opt-in for an infra node
            AllowCoverTrafficOverTor = onionOnly && _options.EnableCoverTraffic,   // this flag only affects TorOnly mode
            EnableFerryman = !onionOnly && _options.EnableFerryman,   // broker clearnet hole punches (no clearnet path in onion-only)
            EnableEffigies = false,
            EnablePageants = false,
            MaxPageantsAsMember = _options.EnableCoverTraffic ? 4 : 0,
            Power = PowerProfile.Unmetered,
        }, ct).ConfigureAwait(false);

        return node;
    }

    /// <summary>Log who we are and, importantly, our own connection link — the one operators share to grow the network.</summary>
    private void AnnounceSelf(CupriNode node, string dataDir, CancellationToken ct)
    {
        _log.LogInformation("Lodestar online for network '{Network}'.", _options.Concordium);
        if (!string.IsNullOrWhiteSpace(_options.Moniker))
            _log.LogInformation("Advertising Moniker '{Moniker}' (self-asserted, unverified — peers trust it only via the fingerprint).", Monikers.Normalize(_options.Moniker));
        _log.LogInformation("Node key (Sigil): {Sigil}", Hex(node.Identity.Sigil));
        _log.LogInformation("Fingerprint: {Fingerprint}", Bech32.Fingerprint(node.Identity.Sigil));
        _log.LogInformation("Listening on {Endpoint}.", node.LocalEndPoint);
        _log.LogInformation("Known peers loaded from hot path: {Count}", node.Constellation.Count);
        if (_options.EnableFerryman && !_options.TorOnly)
            _log.LogInformation("Ferryman relay: ON — brokering hole punches for NAT'd peers (signaling only, no channel content).");

        // Onion-only: the link has no reachable address until the onion publishes, so wait for it. Dual-stack:
        // the clearnet link works immediately, so log it now and just add the .onion to it once it publishes.
        var torEnabled = _options.EnableTor || _options.TorOnly;
        if (torEnabled && node.OnionBeacon is null)
        {
            if (_options.TorOnly)
            {
                _log.LogInformation(
                    "Onion-only: the .onion connection link will appear here (and on the status page) once the " +
                    "onion service is published — Tor bootstrap can take a minute or two.");
            }
            else
            {
                WriteSelfLink(node, dataDir); // the clearnet half is usable right away
                _log.LogInformation(
                    "Tor also enabled: the .onion will be added to this node's link (status page / lodestar.link) " +
                    "once the onion service publishes.");
            }
            _ = Task.Run(() => AnnounceWhenOnionReadyAsync(node, dataDir, ct), ct);
            return;
        }

        WriteSelfLink(node, dataDir);
    }

    private async Task AnnounceWhenOnionReadyAsync(CupriNode node, string dataDir, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && node.OnionBeacon is null)
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
                WriteSelfLink(node, dataDir);
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private void WriteSelfLink(CupriNode node, string dataDir)
    {
        string link;
        try
        {
            link = node.IntoneUri(TimeSpan.FromHours(_options.SelfLinkLifetimeHours), DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not mint this node's own link.");
            return;
        }

        // Prominent, easy to copy from console/journal — this is the link others use to join our network.
        _log.LogInformation("This node's connection link (share it so others can join / seed from us):");
        _log.LogInformation("    {Link}", link);
        if (!_options.EnableTor && !_options.TorOnly
            && string.IsNullOrWhiteSpace(_options.PublicHost) && _options.AdvertisedAddresses.Count == 0 && !_options.EnablePortMapping)
            _log.LogWarning(
                "No PublicHost / AdvertisedAddresses set and port mapping is off, so the link advertises the bind " +
                "address only. Set 'Lodestar:PublicHost' (and PublicPort) or 'Lodestar:AdvertisedAddresses' to a " +
                "reachable address for an externally-usable link.");

        if (_options.WriteSelfLink)
        {
            try
            {
                var path = Path.Combine(dataDir, "lodestar.link");
                File.WriteAllText(path, link + Environment.NewLine);
                _log.LogInformation("Wrote this node's link to {Path}", path);
            }
            catch (Exception ex) { _log.LogDebug(ex, "Could not write lodestar.link."); }
        }
    }

    private async Task BootstrapFromSeedsAsync(CupriNode node, CancellationToken ct)
    {
        var seeds = SeedCollector.Collect(_options, _commandLineArgs, _log);
        if (seeds.Count == 0)
        {
            if (node.Constellation.Count == 0)
                _log.LogInformation(
                    "No seed links and no known peers — this Lodestar is standing up a NEW network (genesis). " +
                    "Share the link above so other nodes can seed from us.");
            else
                _log.LogInformation("No seed links provided; warm-starting from {Count} known peer(s).", node.Constellation.Count);
            return;
        }

        _log.LogInformation("Bootstrapping from {Count} seed link(s)…", seeds.Count);
        var gate = new SemaphoreSlim(Math.Max(1, _options.SeedConnectConcurrency));
        var ok = 0;
        var fail = 0;

        await Task.WhenAll(seeds.Select(async seed =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!IntonationUri.TryParse(seed, out var intonation, out _))
                {
                    Interlocked.Increment(ref fail);
                    _log.LogWarning("Malformed seed link ignored: {Seed}", SeedCollector.Truncate(seed));
                    return;
                }

                // A Lodestar pairs at L1 to exchange peer records (seeding the overlay), then drops the
                // connection — it never opens a channel. PersistOverlay caches the learned keys to the hot path.
                var peer = await node.ConjoinAsync(intonation, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                await peer.DisposeAsync().ConfigureAwait(false);
                Interlocked.Increment(ref ok);
                _log.LogInformation("Seeded from {Peer}.", Hex(intonation.InviterSigil));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref fail);
                _log.LogWarning("Seed link failed: {Reason}", ex.Message);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        _log.LogInformation(
            "Seed bootstrap complete: {Ok} connected, {Fail} failed. Constellation now holds {Count} peer(s).",
            ok, fail, node.Constellation.Count);

        // Persist the freshly-learned keys immediately so even a crash right now leaves a warm hot path.
        try { await node.SaveOverlayStateAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "Post-bootstrap overlay save failed."); }
    }

    /// <summary>Serve inbound overlay-control connections forever. Any channel-pairing attempt is dropped — no L2 here.</summary>
    private async Task AcceptLoopAsync(CupriNode node, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PairedPeer peer;
            try
            {
                peer = await node.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Accept iteration failed; continuing to serve.");
                continue;
            }

            // Overlay control (gossip, adverts, discovery) is served inside AcceptAsync; a returned peer is a
            // channel-pairing attempt. A Lodestar carries no L2, so we close it.
            _log.LogDebug("Dropped an L1 pairing from {Peer} (Lodestar carries no channel/L2 traffic).", Hex(peer.PeerSigil));
            await peer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task MaintenanceLoopAsync(CupriNode node, long startedAt, CancellationToken ct)
    {
        // Prompt warm-up: re-check known peers immediately rather than waiting a full gossip interval.
        try { await node.GossipOnceAsync(_options.GossipFanout, 16, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) { _log.LogDebug(ex, "Warm-up gossip failed."); }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var uptime = Stopwatch.GetElapsedTime(startedAt);
            _log.LogInformation(
                "Lodestar alive — {Count} known peer(s), uptime {Uptime:d\\.hh\\:mm\\:ss}.",
                node.Constellation.Count, uptime);
            try { await node.SaveOverlayStateAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "Periodic overlay save failed."); }
        }
    }

    /// <summary>
    /// The reachable addresses to advertise in this node's link: <see cref="LodestarOptions.PublicHost"/> plus any
    /// operator-supplied <see cref="LodestarOptions.AdvertisedAddresses"/> (for a bootstrap situation where the
    /// service has public IPs it can't discover itself). Null when none are configured (advertise the bind address).
    /// </summary>
    private IReadOnlyList<Beacon>? BuildAdvertisedBeacons()
    {
        var defaultPort = _options.PublicPort ?? _options.ListenPort;
        var beacons = new List<Beacon>();

        if (!string.IsNullOrWhiteSpace(_options.PublicHost))
            beacons.Add(new Beacon(EndpointKind.Host, _options.PublicHost!, defaultPort));

        foreach (var entry in _options.AdvertisedAddresses)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            if (!TryParseHostPort(entry, defaultPort, out var host, out var port))
            {
                _log.LogWarning("Ignoring malformed advertised address '{Entry}' (use host or host:port).", entry);
                continue;
            }
            beacons.Add(new Beacon(EndpointKind.Manual, host, port));
        }

        return beacons.Count > 0 ? beacons : null;
    }

    private async Task WebLoopAsync(CupriNode node, string dataDir, CancellationToken ct)
    {
        var refresh = Math.Max(5, _options.WebRefreshSeconds);
        var provider = new LodestarLinkProvider(
            node, TimeSpan.FromHours(_options.SelfLinkLifetimeHours), TimeSpan.FromSeconds(refresh));

        // Split only makes sense for a dual-stack node (clearnet + onion). It serves the clearnet page a
        // clearnet-only link and publishes a separate onion that serves a Tor-only link — so a Tor visitor is
        // never shown the clearnet IP. On by default (safety); off falls back to a single all-transports page.
        var dualStack = _options.EnableTor && !_options.TorOnly && _onion is not null;
        var split = dualStack && _options.WebSplit;

        var clearnetFace = split ? LinkTransports.ClearnetOnly : LinkTransports.All;
        int? torFacePort = split ? FreeLocalPort() : null;

        var server = new LodestarWebServer(provider, node, _options.Concordium, refresh, clearnetFace, torFacePort, _log, _options.WebDebug);

        if (split && torFacePort is int tfp)
            _ = Task.Run(() => PublishWebOnionAsync(node, server, dataDir, tfp, ct), ct);

        try
        {
            await server.RunAsync(_options.WebListenAddress, _options.WebPort, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            // The status page is auxiliary: never let it bring the node down.
            _log.LogError(ex, "Status page stopped unexpectedly; the node keeps running.");
        }
    }

    /// <summary>
    /// Once Tor is up, publishes the status page as its own onion (forwarding to the local tor-face port the web
    /// server already listens on) and surfaces the address — logs it, writes <c>web.onion</c>, and shows it on the
    /// clearnet page. Best-effort: if it fails, the clearnet page still works.
    /// </summary>
    private async Task PublishWebOnionAsync(CupriNode node, LodestarWebServer server, string dataDir, int torFacePort, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && node.OnionBeacon is null)
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || _onion is null)
                return;

            var address = await _onion.PublishAuxiliaryOnionAsync("tor/web-onion-service-key", torFacePort, ct).ConfigureAwait(false);
            server.TorPageAddress = address;
            _log.LogInformation("Status page also reachable over Tor at: http://{Onion}/ (serves an onion-only link).", address);
            try { File.WriteAllText(Path.Combine(dataDir, "web.onion"), address + Environment.NewLine); }
            catch (Exception ex) { _log.LogDebug(ex, "Could not write web.onion."); }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not publish the status page's Tor onion; the clearnet page still works.");
        }
    }

    private static int FreeLocalPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Parses <c>host</c> or <c>host:port</c> (bracket IPv6). A bare host uses <paramref name="defaultPort"/>.</summary>
    private static bool TryParseHostPort(string text, int defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort;
        text = text.Trim();
        if (text.Length == 0)
            return false;

        if (text.StartsWith('['))                       // [ipv6] or [ipv6]:port
        {
            var end = text.IndexOf(']');
            if (end <= 1)
                return false;
            host = text[1..end];
            var rest = text[(end + 1)..];
            if (rest.Length == 0)
                return IPAddress.TryParse(host, out _);
            return rest[0] == ':' && int.TryParse(rest[1..], out port) && port is > 0 and <= 65535 && IPAddress.TryParse(host, out _);
        }

        var colon = text.LastIndexOf(':');
        if (colon < 0 || text.IndexOf(':') != colon)    // no colon = bare host; multiple = unbracketed IPv6 (reject)
        {
            host = text;
            return colon < 0 && host.Length > 0;
        }

        host = text[..colon];
        return host.Length > 0 && int.TryParse(text[(colon + 1)..], out port) && port is > 0 and <= 65535;
    }

    private string ResolveDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.DataDirectory))
            return _options.DataDirectory;

        // systemd sets STATE_DIRECTORY when the unit declares StateDirectory=; honour it (it may be colon-separated).
        var stateDir = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(stateDir))
            return stateDir.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)[0];

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CupriNet.Lodestar");

        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    private static IPAddress ParseAddress(string value)
        => value.Equals("any", StringComparison.OrdinalIgnoreCase) ? IPAddress.Any : IPAddress.Parse(value);

    private static string Hex(Sigil sigil) => Convert.ToHexStringLower(sigil.Span);
}
