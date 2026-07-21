using System.Collections.Concurrent;
using CupriNet.Abstractions;
using CupriNet.Arcanum;
using CupriNet.Codex;
using CupriNet.Concordance;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Vessel;

namespace CupriNet.Hosting;

/// <summary>
/// The live overlay (L1 Concordance) plane: this node serves discovery requests from others and issues its
/// own over real, Noise-encrypted connections — publishing channel advertisements near a channel's Ascendant
/// and finding providers by routing a Divination toward it. This is what turns the (previously simulation-only)
/// discovery algorithms into something that runs over the network.
/// </summary>
public sealed partial class CupriNode
{
    private readonly DecreeStore _decrees = new();
    private readonly ConcurrentDictionary<Sigil, ControlConnection> _controlPool = new();
    private int _activeControlConnections;

    /// <summary>A signed, self-describing record of this node (its dialable beacons + capabilities) to seed peers with.</summary>
    public PeerRecord SelfRecord(DateTimeOffset now)
        => PeerRecordSigner.Create(Identity, SelfBeacons(), (ulong)now.ToUnixTimeMilliseconds(), PeerCapabilities.ChannelProvider, Suite, now);

    /// <summary>Admits a (validated) peer record into the Constellation so overlay lookups have somewhere to start.</summary>
    public bool AdmitPeer(PeerRecord record, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!PeerRecordSigner.Verify(record, Suite))
            return false;
        Constellation.Admit(record, PeerBucket.Wayfarers, now, "seed");
        return true;
    }

    /// <summary>
    /// Advertises this node as a provider of a channel: routes a signed Decree toward the channel's Ascendant
    /// and stores it at the reached holder nodes (and locally). Returns how many holders accepted it.
    /// </summary>
    public async Task<int> PublishChannelAsync(Watchword watchword, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watchword);
        var keys = ArcanumKeys.Derive(watchword, Suite);
        var decree = DecreeSigner.Publish(Identity, keys, SelfBeacons(), TimeSpan.FromHours(1), (ulong)now.ToUnixTimeMilliseconds(), Suite, now);

        _decrees.Publish(decree, now); // we hold our own advert too

        var seeds = Constellation.Sample(64);
        if (seeds.Count == 0)
            return 0;
        return await ChannelDirectory.PublishAsync(
            keys.Ascendant, decree, seeds, QueryAuguryAsync, PublishToHolderAsync,
            options: null, isAnchored: Constellation.IsAnchored, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds providers of a channel over the network: routes a Divination toward its Ascendant, queries the
    /// reached nodes for Decrees matching the current Glyph window, and returns the verified, matching ones.
    /// Each returned Decree names a provider Sigil and its dialable beacons.
    /// </summary>
    public async Task<IReadOnlyList<Decree>> FindChannelProvidersAsync(Watchword watchword, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watchword);
        var keys = ArcanumKeys.Derive(watchword, Suite);
        var seeds = Constellation.Sample(64);
        if (seeds.Count == 0)
            return [];
        return await ChannelDirectory.FindProvidersAsync(
            keys, now, Suite, seeds, QueryAuguryAsync, LookupFromHolderAsync,
            options: null, isAnchored: Constellation.IsAnchored, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort overlay bootstrap after a channel pairing: exchange signed self-records so each side
    /// seeds the other into its Constellation. This is what lets a node that joins via a single link then
    /// route overlay discovery through the peer it just met.
    /// </summary>
    private async Task BootstrapOverlayAsync(IVessel vessel, bool initiator, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_options.EnablePeerExchange)
            return;
        try
        {
            using var timed = LinkedTimeout(cancellationToken, TimeSpan.FromSeconds(10));
            var mine = PeerRecordCodec.Encode(SelfRecord(now));
            // Initiator sends first, responder reads first — a deadlock-free ordering.
            if (initiator)
            {
                await vessel.SendAsync(PeerExchange.DefaultStream, mine, timed.Token).ConfigureAwait(false);
                AdmitPeer(await ReceiveRecordAsync(vessel, timed.Token).ConfigureAwait(false), now);
            }
            else
            {
                AdmitPeer(await ReceiveRecordAsync(vessel, timed.Token).ConfigureAwait(false), now);
                await vessel.SendAsync(PeerExchange.DefaultStream, mine, timed.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Enrichment only — a failed bootstrap must never fail the pairing.
        }
    }

    private static async Task<PeerRecord> ReceiveRecordAsync(IVessel vessel, CancellationToken cancellationToken)
    {
        var frame = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new CupriNodeException("Vessel closed during overlay bootstrap.");
        if (frame.StreamId != PeerExchange.DefaultStream)
            throw new CupriNodeException($"Unexpected frame on stream {frame.StreamId} during overlay bootstrap.");
        return PeerRecordCodec.Decode(frame.Payload).Record;
    }

    // ---- Session-kind marker (declared by the initiator right after the Noise handshake) --------

    private static ValueTask DeclareSessionKindAsync(IVessel vessel, byte kind, CancellationToken cancellationToken)
        => vessel.SendAsync(OverlayControl.Stream, new[] { kind }, cancellationToken);

    private static async Task<byte> ReadSessionKindAsync(IVessel vessel, CancellationToken cancellationToken)
    {
        var frame = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new CupriNodeException("Vessel closed before the session-kind marker.");
        if (frame.StreamId != OverlayControl.Stream || frame.Payload.Length < 1)
            throw new CupriNodeException("Malformed session-kind marker.");
        return frame.Payload[0];
    }

    // ---- Server side: answer control requests from the local Constellation / DecreeStore ---------

    private async Task ServeControlAsync(IVessel vessel, CancellationToken cancellationToken)
    {
        var limiter = new ControlRateLimiter(_options.MaxControlRequestsPerWindow, _options.ControlWindowSeconds * 1000L);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                    break;
                if (frame.Value.StreamId != OverlayControl.Stream || frame.Value.Payload.Length == 0)
                    continue;
                if (!limiter.Allow(Environment.TickCount64))
                    break; // request flood on this connection — drop it
                var response = HandleControlRequest(frame.Value.Payload);
                await vessel.SendAsync(OverlayControl.Stream, response, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Connection closed or a malformed exchange — drop it.
        }
        finally
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private byte[] HandleControlRequest(byte[] payload)
    {
        var now = DateTimeOffset.UtcNow;
        var op = payload[0];
        var body = payload.AsSpan(1);
        switch (op)
        {
            case OverlayControl.OpDivine:
            {
                if (body.Length < RoutingKey.Size)
                    return OverlayControl.StatusResponse(OverlayControl.StatusRejected);
                var target = new RoutingKey(body[..RoutingKey.Size]);
                return OverlayControl.PeerRecordsResponse(Constellation.ClosestTo(target, OverlayControl.MaxAugury));
            }
            case OverlayControl.OpPublish:
            {
                try
                {
                    var decree = DecreeCodec.Decode(body).Decree;
                    if (DecreeSigner.Verify(decree, Suite))
                    {
                        _decrees.Publish(decree, now);
                        return OverlayControl.StatusResponse(OverlayControl.StatusOk);
                    }
                }
                catch { /* malformed */ }
                return OverlayControl.StatusResponse(OverlayControl.StatusRejected);
            }
            case OverlayControl.OpLookup:
            {
                try
                {
                    var reader = new CodexReader(body);
                    var count = reader.ReadVarUInt();
                    if (count > (ulong)OverlayControl.MaxLookupGlyphs)
                        return OverlayControl.DecreesResponse([]);
                    var glyphs = new List<byte[]>((int)count);
                    for (var i = 0UL; i < count; i++)
                        glyphs.Add(reader.ReadBytes().ToArray());
                    return OverlayControl.DecreesResponse(_decrees.LookupWindow(glyphs, now));
                }
                catch { return OverlayControl.DecreesResponse([]); }
            }
            default:
                return OverlayControl.StatusResponse(OverlayControl.StatusRejected);
        }
    }

    // ---- Client side: the delegates Divination/ChannelDirectory call, over real connections -------

    private async Task<IReadOnlyList<PeerRecord>> QueryAuguryAsync(PeerRecord peer, RoutingKey target, CancellationToken cancellationToken)
    {
        try
        {
            var conn = await GetControlAsync(peer, cancellationToken).ConfigureAwait(false);
            var response = await conn.RoundtripAsync(OverlayControl.DivineRequest(target), cancellationToken).ConfigureAwait(false);
            return OverlayControl.ParsePeerRecords(response, Suite);
        }
        catch (OperationCanceledException) { throw; }
        catch { EvictControl(peer.Sigil); return []; }
    }

    private async Task PublishToHolderAsync(PeerRecord holder, Decree decree, CancellationToken cancellationToken)
    {
        try
        {
            var conn = await GetControlAsync(holder, cancellationToken).ConfigureAwait(false);
            await conn.RoundtripAsync(OverlayControl.PublishRequest(decree), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { EvictControl(holder.Sigil); }
    }

    private async Task<IReadOnlyList<Decree>> LookupFromHolderAsync(PeerRecord holder, IReadOnlyList<byte[]> glyphWindow, CancellationToken cancellationToken)
    {
        try
        {
            var conn = await GetControlAsync(holder, cancellationToken).ConfigureAwait(false);
            var response = await conn.RoundtripAsync(OverlayControl.LookupRequest(glyphWindow), cancellationToken).ConfigureAwait(false);
            return OverlayControl.ParseDecrees(response);
        }
        catch (OperationCanceledException) { throw; }
        catch { EvictControl(holder.Sigil); return []; }
    }

    private async Task<ControlConnection> GetControlAsync(PeerRecord peer, CancellationToken cancellationToken)
    {
        var sigil = peer.Sigil;
        if (_controlPool.TryGetValue(sigil, out var pooled))
            return pooled;

        var beacon = peer.Endpoints.FirstOrDefault(b => b.Kind is EndpointKind.Host or EndpointKind.Mapped or EndpointKind.Manual)
                     ?? throw new CupriNodeException("Peer has no dialable beacon.");

        var vessel = await TcpVessel.ConnectAsync(beacon.Host, beacon.Port, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            using var timed = LinkedHandshakeToken(cancellationToken);
            if (_options.EnableToll)
                await Toll.SolveAsync(vessel, timed.Token).ConfigureAwait(false);
            var conjunction = await NoiseConjunction.InitiateAsync(vessel, Identity, Network, Suite, expectedPeer: sigil, cancellationToken: timed.Token).ConfigureAwait(false);
            await DeclareSessionKindAsync(conjunction.Vessel, OverlayControl.KindControl, timed.Token).ConfigureAwait(false);

            var conn = new ControlConnection(conjunction.Vessel);
            if (_controlPool.TryGetValue(sigil, out var raced))
            {
                await conn.DisposeAsync().ConfigureAwait(false); // lost the race — reuse the pooled one
                return raced;
            }
            _controlPool[sigil] = conn;
            return conn;
        }
        catch
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void EvictControl(Sigil sigil)
    {
        if (_controlPool.TryRemove(sigil, out var conn))
            _ = conn.DisposeAsync();
    }

    private IReadOnlyList<Beacon> SelfBeacons()
    {
        var beacons = new List<Beacon> { new(EndpointKind.Host, _advertiseHost, LocalEndPoint.Port) };
        var mapped = ReflexiveObserver.MappedBeacon();
        if (mapped is not null && !beacons.Any(b => b.Kind == mapped.Kind && b.Host == mapped.Host && b.Port == mapped.Port))
            beacons.Add(mapped);
        return beacons;
    }

    private async ValueTask DisposeOverlayAsync()
    {
        foreach (var conn in _controlPool.Values)
        {
            try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        }
        _controlPool.Clear();
    }
}
