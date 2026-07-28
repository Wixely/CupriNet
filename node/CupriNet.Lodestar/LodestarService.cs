using System.Diagnostics;
using System.Net;
using CupriNet.Abstractions;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Persistence;
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
            AnnounceSelf(node, dataDir);
            await BootstrapFromSeedsAsync(node, stoppingToken).ConfigureAwait(false);

            // Serve inbound overlay control forever, and keep the map fresh / persisted, until shutdown.
            await Task.WhenAll(
                AcceptLoopAsync(node, stoppingToken),
                MaintenanceLoopAsync(node, startedAt, stoppingToken)).ConfigureAwait(false);
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

        IReadOnlyList<Beacon>? advertised = null;
        if (!string.IsNullOrWhiteSpace(_options.PublicHost))
            advertised = new[] { new Beacon(EndpointKind.Host, _options.PublicHost, _options.PublicPort ?? _options.ListenPort) };

        var node = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = _options.Concordium,
            ListenAddress = ParseAddress(_options.ListenAddress),
            ListenPort = _options.ListenPort,
            Suite = suite,
            SecretStore = store,
            AdvertisedBeacons = advertised,
            PersistOverlay = true,               // warm start: reload known peers' keys from the hot path
            EnableOverlayGossip = true,          // keep the map fresh and re-check known peers
            OverlayGossipIntervalSeconds = _options.GossipIntervalSeconds,
            OverlayGossipFanout = _options.GossipFanout,
            EnableLanDiscovery = _options.EnableLanDiscovery,
            EnablePortMapping = _options.EnablePortMapping,
            AllowedSubnets = _options.AllowedSubnets.Count > 0 ? _options.AllowedSubnets : null,
            DeniedSubnets = _options.DeniedSubnets.Count > 0 ? _options.DeniedSubnets : null,
            EnableHotFuzz = _options.EnableCoverTraffic,   // cover traffic is opt-in for an infra node
            EnableEffigies = false,
            EnablePageants = false,
            MaxPageantsAsMember = _options.EnableCoverTraffic ? 4 : 0,
            Power = PowerProfile.Unmetered,
        }, ct).ConfigureAwait(false);

        return node;
    }

    /// <summary>Log who we are and, importantly, our own connection link — the one operators share to grow the network.</summary>
    private void AnnounceSelf(CupriNode node, string dataDir)
    {
        _log.LogInformation("Lodestar online for network '{Network}'.", _options.Concordium);
        _log.LogInformation("Node key (Sigil): {Sigil}", Hex(node.Identity.Sigil));
        _log.LogInformation("Listening on {Endpoint}.", node.LocalEndPoint);
        _log.LogInformation("Known peers loaded from hot path: {Count}", node.Constellation.Count);

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
        if (string.IsNullOrWhiteSpace(_options.PublicHost) && !_options.EnablePortMapping)
            _log.LogWarning(
                "No PublicHost set and port mapping is off, so the link advertises the bind address only. " +
                "Set 'Lodestar:PublicHost' (and PublicPort) to a reachable address for an externally-usable link.");

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
