using CupriNet.Abstractions;
using CupriNet.Hosting;
using CupriNet.Vessel;
using CupriTor;          // TorClient, TorClientOptions, OnionServiceKey, OnionAddress
using CupriTor.Protocol; // IStateStore

namespace CupriNet.Tor;

/// <summary>
/// A concrete <see cref="IOnionTransport"/> backed by CupriTor: publishes a stable v3 onion service (guards +
/// vanguards persisted in CupriNet's encrypted SecretStore) and dials peers' <c>.onion</c> addresses, handing the
/// resulting streams to CupriNet as vessels via <see cref="StreamVessel.Over"/>. Onion-only (never clearnet).
/// </summary>
public sealed class CupriTorOnionTransport : IOnionTransport
{
    private const string ServiceKeyId = "tor/onion-service-key";

    private readonly TorClient _tor;
    private readonly OnionServiceKey _serviceKey;
    private readonly ISecretStore _secrets;
    private readonly List<IAsyncDisposable> _auxHosts = new();
    private IAsyncDisposable? _serviceHost;

    /// <inheritdoc/>
    public event Action<string>? Status;

    private CupriTorOnionTransport(TorClient tor, OnionServiceKey serviceKey, ISecretStore secrets)
    {
        _tor = tor;
        _serviceKey = serviceKey;
        _secrets = secrets;
        // Forward Tor's bootstrap/connect progress as a friendly "[nn%] message" line.
        _tor.StatusChanged += (_, s) =>
        {
            var pct = (int)Math.Round(Math.Clamp(s.Progress, 0, 1) * 100);
            var text = string.IsNullOrWhiteSpace(s.Message) ? s.Phase.ToString() : s.Message;
            Status?.Invoke($"[{pct}%] {text}");
        };
    }

    /// <summary>
    /// Builds the transport, loading (or creating + persisting) a stable onion identity in <paramref name="secrets"/>
    /// so our <c>.onion</c> survives restarts, and backing Tor's guard/vanguard state with the same encrypted store.
    /// </summary>
    public static async Task<CupriTorOnionTransport> CreateAsync(ISecretStore secrets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        var stored = await secrets.LoadAsync(ServiceKeyId, cancellationToken).ConfigureAwait(false);
        var key = stored is not null ? OnionServiceKey.FromTorSecretKey(stored) : OnionServiceKey.CreateRandom();
        if (stored is null)
            await secrets.StoreAsync(ServiceKeyId, key.ToTorSecretKey(), cancellationToken).ConfigureAwait(false);

        var options = new TorClientOptions
        {
            OnionOnly = true, // hard-disable the clearnet exit path — CupriNet must never leave Tor
            StateStore = new SecretBackedStateStore(new SecretStoreBlobStore(secrets)), // guards + vanguards, encrypted
        };
        return new CupriTorOnionTransport(new TorClient(options), key, secrets);
    }

    /// <summary>
    /// Publishes an ADDITIONAL onion service — its own persistent identity, keyed by <paramref name="keyId"/> in the
    /// secret store — forwarding to <c>127.0.0.1:<paramref name="localPort"/></c> on the same Tor client, and returns
    /// its <c>.onion</c> address. Use it to expose an auxiliary local service (e.g. a status page) over Tor, separate
    /// from the overlay onion. Requires the transport to have started (Tor bootstrapped).
    /// </summary>
    public async Task<string> PublishAuxiliaryOnionAsync(string keyId, int localPort, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        var stored = await _secrets.LoadAsync(keyId, cancellationToken).ConfigureAwait(false);
        var key = stored is not null ? OnionServiceKey.FromTorSecretKey(stored) : OnionServiceKey.CreateRandom();
        if (stored is null)
            await _secrets.StoreAsync(keyId, key.ToTorSecretKey(), cancellationToken).ConfigureAwait(false);

        var host = await _tor.PublishOnionAsync(
            key, "127.0.0.1", localPort, introPoints: 3, authorizedClients: null, cancellationToken).ConfigureAwait(false);
        lock (_auxHosts)
            _auxHosts.Add(host);
        return key.OnionAddress;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => _tor.StartAsync(cancellationToken);

    public async Task<IVessel> ConnectAsync(string onionAddress, int virtualPort, CancellationToken cancellationToken = default)
    {
        if (!OnionAddress.TryParse(onionAddress, out _))
            throw new ArgumentException($"Not a valid v3 .onion address: {onionAddress}", nameof(onionAddress));

        var stream = await _tor.ConnectToOnionAsync(onionAddress, virtualPort, cancellationToken).ConfigureAwait(false);
        return StreamVessel.Over(stream, onDispose: () => { stream.Dispose(); return ValueTask.CompletedTask; });
    }

    public async Task<string> PublishAsync(int virtualPort, int localPort, CancellationToken cancellationToken = default)
    {
        // Reverse-proxy inbound onion connections to our local TCP listener, so they arrive through the node's
        // ordinary AcceptAsync path. Awaiting this == "descriptor uploaded to ≥1 responsible HSDir".
        // (virtualPort is what we advertise; CupriTor's reverse proxy forwards inbound streams to the backend.)
        _ = virtualPort;
        _serviceHost = await _tor.PublishOnionAsync(
            _serviceKey, "127.0.0.1", localPort, introPoints: 3, authorizedClients: null, cancellationToken).ConfigureAwait(false);
        return _serviceKey.OnionAddress;
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceHost is not null)
        {
            try { await _serviceHost.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort unpublish */ }
        }
        List<IAsyncDisposable> aux;
        lock (_auxHosts)
            aux = [.. _auxHosts];
        foreach (var host in aux)
        {
            try { await host.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort unpublish */ }
        }
        await _tor.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Adapts CupriNet's encrypted <see cref="SecretStoreBlobStore"/> to CupriTor's <see cref="IStateStore"/>
/// (entry guards + layer-2 vanguards), so that anonymity-critical state persists in the encrypted store.</summary>
internal sealed class SecretBackedStateStore(SecretStoreBlobStore inner) : IStateStore
{
    public byte[]? Read(string key) => inner.Read(key);
    public void Write(string key, byte[] data) => inner.Write(key, data);
}
