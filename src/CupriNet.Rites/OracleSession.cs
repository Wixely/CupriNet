using CupriNet.Alembic;
using CupriNet.Codex;
using CupriNet.Vessel;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.Rites;

/// <summary>An event read from an Oracle stream.</summary>
public abstract record OracleEvent;

/// <summary>A request arrived (the Shrine host side).</summary>
public sealed record RequestReceived(OracleRequest Request) : OracleEvent;

/// <summary>A response arrived (the Pilgrim side).</summary>
public sealed record ResponseReceived(OracleResponse Response) : OracleEvent;

/// <summary>
/// Carries the Oracle rite — an HTTP-shaped request/response — as Veil-sealed frames over a stream channel. A
/// Pilgrim <see cref="ConsultAsync"/>s (sends a request, reads the response); a Shrine host reads requests with
/// <see cref="ReceiveAsync"/> and answers with <see cref="SendResponseAsync"/>. Use a <see cref="VesselMux"/>
/// stream to run it alongside other rites. The session is already authenticated (the Pilgrimage handshake) and
/// encrypted (the Veil), so frames carry no per-frame author envelope.
/// </summary>
public sealed class OracleSession
{
    /// <summary>Logical stream for the Oracle rite (0 Conjunction, 1 peer exchange, 2 Consecration, 3 Epistles, 4 Conduits).</summary>
    public const ushort RequestStream = 5;

    private enum FrameType : byte
    {
        Request = 1,
        Response = 2,
    }

    private readonly IStreamChannel _channel;
    private readonly VeilCipher? _veil;

    public OracleSession(IStreamChannel channel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        ArgumentNullException.ThrowIfNull(suite);
        _veil = new VeilCipher(sessionKey, suite);
    }

    /// <summary>Convenience: bind to a single Vessel stream directly (single-stream use only).</summary>
    public OracleSession(VesselSession vessel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite, ushort stream = RequestStream)
        : this(new VesselStreamChannel(vessel, stream), sessionKey, suite)
    {
    }

    /// <summary>
    /// Binds over an <em>already-encrypted</em> transport (e.g. the NoiseVessel from a Pilgrimage): frames are sent
    /// as-is, with no Veil layer, because the underlying vessel is itself the confidentiality + authenticity boundary.
    /// </summary>
    public OracleSession(IStreamChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _veil = null;
    }

    /// <summary>Convenience: bind over an already-encrypted Vessel's stream directly (single-stream use only).</summary>
    public OracleSession(IVessel vessel, ushort stream = RequestStream)
        : this(new VesselStreamChannel(vessel, stream))
    {
    }

    public Task SendRequestAsync(OracleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendFrameAsync(FrameType.Request, OracleCodec.EncodeRequest(request), cancellationToken);
    }

    public Task SendResponseAsync(OracleResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        return SendFrameAsync(FrameType.Response, OracleCodec.EncodeResponse(response), cancellationToken);
    }

    /// <summary>Reads the next Oracle event, or null when the peer closes the stream.</summary>
    public async Task<OracleEvent?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var payload = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (payload is null)
            return null;

        var reader = new CodexReader(payload);
        var type = (FrameType)reader.ReadByte();
        var body = reader.ReadBytes();

        var plaintext = _veil is null
            ? body.ToArray()
            : _veil.Open(body) ?? throw new EpistleException("Oracle content failed authentication (Veil).");

        return type switch
        {
            FrameType.Request => new RequestReceived(OracleCodec.DecodeRequest(plaintext)),
            FrameType.Response => new ResponseReceived(OracleCodec.DecodeResponse(plaintext)),
            _ => throw new EpistleException($"Unknown Oracle frame type {(byte)type}."),
        };
    }

    /// <summary>Pilgrim convenience: consult the Shrine — send a request and read the next response.</summary>
    public async Task<OracleResponse> ConsultAsync(OracleRequest request, CancellationToken cancellationToken = default)
    {
        await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        var evt = await ReceiveAsync(cancellationToken).ConfigureAwait(false)
                  ?? throw new EpistleException("Oracle stream closed before a response arrived.");
        return evt is ResponseReceived received
            ? received.Response
            : throw new EpistleException("Expected an Oracle response frame.");
    }

    private async Task SendFrameAsync(FrameType type, byte[] body, CancellationToken cancellationToken)
    {
        var w = new CodexWriter();
        w.WriteByte((byte)type);
        w.WriteBytes(_veil is null ? body : _veil.Seal(body));
        await _channel.SendAsync(w.ToArray(), cancellationToken).ConfigureAwait(false);
    }
}
