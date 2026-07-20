using System.Net;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Arcanum;
using CupriNet.Concordance;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Persistence;
using CupriNet.Rites;
using CupriNet.Traversal;
using CupriNet.Vessel;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.Hosting;

/// <summary>Thrown when a node operation fails (invalid Intonation, no dialable endpoint, etc.).</summary>
public sealed class CupriNodeException(string message) : Exception(message);

/// <summary>
/// The public entry point to CupriNet: one object that ties identity + persistence, the Vessel transport,
/// Conjunction pairing, the Concordance overlay, and the Arcanum/rite layers together. Mint an Intonation
/// to invite a peer, Conjoin to an Intonation to pair, then Consecrate a channel to exchange messages.
/// </summary>
public sealed class CupriNode : IAsyncDisposable
{
    /// <summary>Maximum time a transport handshake (Noise + identity binding + reflexion) may take.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly CupriNodeOptions _options;
    private readonly VesselListener _listener;
    private readonly string _advertiseHost;
    private readonly byte[] _tollSecret = Toll.NewSecret();

    private CupriNode(CupriNodeOptions options, ICryptoSuite suite, NodeIdentity identity, VesselListener listener)
    {
        _options = options;
        Suite = suite;
        Identity = identity;
        _listener = listener;
        Network = new Concordium(options.Concordium);
        Constellation = new Constellation();
        _advertiseHost = options.ListenAddress.Equals(IPAddress.Any) ? "127.0.0.1" : options.ListenAddress.ToString();
    }

    /// <summary>This node's long-term identity.</summary>
    public NodeIdentity Identity { get; }

    /// <summary>The network this node belongs to.</summary>
    public Concordium Network { get; }

    /// <summary>The crypto suite in use.</summary>
    public ICryptoSuite Suite { get; }

    /// <summary>This node's view of the overlay.</summary>
    public Constellation Constellation { get; }

    /// <summary>Reflexive-endpoint observations gathered from peers during pairing.</summary>
    public ReflexiveObserver ReflexiveObserver { get; } = new();

    /// <summary>The bound local endpoint (reflects the OS-assigned port when 0 was requested).</summary>
    public IPEndPoint LocalEndPoint => _listener.LocalEndPoint;

    /// <summary>Creates and starts a node: sets up crypto and persistence, loads/creates identity, starts listening.</summary>
    public static async Task<CupriNode> CreateAsync(CupriNodeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.Concordium);

        var suite = options.Suite ?? new BouncyCastleSuite();
        var secretStore = options.SecretStore ?? new InMemorySecretStore();
        var identity = await new IdentityStore(secretStore).LoadOrCreateAsync(suite, cancellationToken).ConfigureAwait(false);

        var listener = new VesselListener(new IPEndPoint(options.ListenAddress, options.ListenPort));
        listener.Start();

        return new CupriNode(options, suite, identity, listener);
    }

    /// <summary>Mints a fresh connection URL (Intonation) advertising this node's reachability and seed peers.</summary>
    public Intonation Intone(TimeSpan lifetime, DateTimeOffset now, byte[]? petition = null)
    {
        var beacons = new List<Beacon>(_options.AdvertisedBeacons ?? [new Beacon(EndpointKind.Host, _advertiseHost, LocalEndPoint.Port)]);
        var mapped = ReflexiveObserver.MappedBeacon();
        if (mapped is not null && !beacons.Any(b => b.Kind == mapped.Kind && b.Host == mapped.Host && b.Port == mapped.Port))
            beacons.Add(mapped);
        var litany = Constellation.Sample(IntonationCodec.MaxLitany).Select(r => r.Sigil).ToList();

        return IntonationMint.Intone(Identity, Suite, new IntonationOptions
        {
            Network = Network,
            Beacons = beacons,
            Litany = litany,
            Lifetime = lifetime,
            Petition = petition,
        }, now);
    }

    /// <summary>Renders an Intonation as its <c>cuprinet://intone/…</c> URL.</summary>
    public string IntoneUri(TimeSpan lifetime, DateTimeOffset now, byte[]? petition = null)
        => IntonationUri.ToUri(Intone(lifetime, now, petition));

    /// <summary>Validates an Intonation, dials one of its beacons, and completes the Conjunction pairing.</summary>
    public async Task<PairedPeer> ConjoinAsync(Intonation intonation, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intonation);

        var validation = IntonationValidator.ValidateDocument(IntonationCodec.Encode(intonation), Network, Suite, now);
        if (!validation.IsValid)
            throw new CupriNodeException($"Intonation is not usable: {validation.Status}.");

        var beacon = intonation.Beacons.FirstOrDefault(b => b.Kind is EndpointKind.Host or EndpointKind.Mapped or EndpointKind.Manual)
                     ?? throw new CupriNodeException("Intonation has no dialable beacon.");

        var vessel = await TcpVessel.ConnectAsync(beacon.Host, beacon.Port, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            using var timed = LinkedHandshakeToken(cancellationToken);
            if (_options.EnableToll)
                await Toll.SolveAsync(vessel, timed.Token).ConfigureAwait(false);
            var conjunction = await NoiseConjunction.InitiateAsync(
                vessel, Identity, Network, Suite, expectedPeer: intonation.InviterSigil, cancellationToken: timed.Token).ConfigureAwait(false);
            await LearnReflexiveAsync(conjunction.Vessel, initiator: true, cancellationToken).ConfigureAwait(false);
            return new PairedPeer(conjunction.Vessel, conjunction.PeerSigil, conjunction.PeerSealPublicKey, isInitiator: true);
        }
        catch
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Accepts one inbound connection and completes the Conjunction pairing as the responder.</summary>
    public async Task<PairedPeer> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var vessel = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timed = LinkedHandshakeToken(cancellationToken);
            // Issue and verify the pre-handshake Toll before allocating any Noise state (anti-exhaustion).
            if (_options.EnableToll)
                await Toll.IssueAndVerifyAsync(vessel, _tollSecret, vessel.RemoteEndPoint, DateTimeOffset.UtcNow, timed.Token).ConfigureAwait(false);
            var conjunction = await NoiseConjunction.AcceptAsync(vessel, Identity, Network, Suite, timed.Token).ConfigureAwait(false);
            await LearnReflexiveAsync(conjunction.Vessel, initiator: false, cancellationToken).ConfigureAwait(false);
            return new PairedPeer(conjunction.Vessel, conjunction.PeerSigil, conjunction.PeerSealPublicKey, isInitiator: false);
        }
        catch
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Consecrates a channel with a peer using a Watchword, yielding an encrypted channel session.</summary>
    public async Task<ArcanumSession> ConsecrateAsync(PairedPeer peer, Watchword watchword, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(watchword);

        var keys = ArcanumKeys.Derive(watchword, Suite);
        using var timed = LinkedTimeout(cancellationToken, _options.ConsecrationTimeout);
        var consecration = peer.IsInitiator
            ? await ConsecrationHandshake.InitiateAsync(peer.Vessel, keys, Identity.Sigil, peer.PeerSigil, now, Suite, cancellationToken: timed.Token).ConfigureAwait(false)
            : await ConsecrationHandshake.AcceptAsync(peer.Vessel, keys, Identity.Sigil, peer.PeerSigil, now, Suite, cancellationToken: timed.Token).ConfigureAwait(false);

        var author = new RiteIdentity(Identity.Seal.PublicKey, Identity.Seal.PrivateKey);
        return new ArcanumSession(peer.Vessel, consecration.Epoch, consecration.SessionKey, Suite, author, _options.RequireSignedAuthors);
    }

    private static CancellationTokenSource LinkedHandshakeToken(CancellationToken cancellationToken)
        => LinkedTimeout(cancellationToken, HandshakeTimeout);

    /// <summary>A linked token source that also cancels after <paramref name="timeout"/> — the uniform deadline seam.</summary>
    private static CancellationTokenSource LinkedTimeout(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return cts;
    }

    private async Task LearnReflexiveAsync(IVessel vessel, bool initiator, CancellationToken cancellationToken)
    {
        if (!_options.EnableReflexiveDiscovery)
            return;
        try
        {
            using var timed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timed.CancelAfter(TimeSpan.FromSeconds(5));
            var observed = await ReflexiveExchange.ExchangeAsync(vessel, initiator, cancellationToken: timed.Token).ConfigureAwait(false);
            ReflexiveObserver.Add(observed);
        }
        catch
        {
            // best-effort: pairing succeeds even when reflexive discovery is unavailable
        }
    }

    public ValueTask DisposeAsync() => _listener.DisposeAsync();
}
