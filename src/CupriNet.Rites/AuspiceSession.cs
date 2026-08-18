using CupriNet.Alembic;
using CupriNet.Codex;
using CupriNet.Vessel;

namespace CupriNet.Rites;

/// <summary>
/// Carries the Auspice rite — subscribe-once, push-many live feeds — over a stream channel. A Pilgrim
/// <see cref="AttendAsync"/>s a named feed and reads frames; a Shrine reads attendance requests and emanates a
/// <see cref="SendSnapshotAsync"/> followed by <see cref="SendUpdateAsync"/>s. Several feeds share one session,
/// distinguished by their topic.
///
/// <para>Runs on its own logical stream, so a Pilgrim can fetch pages over the <see cref="OracleSession"/> and receive
/// live updates at the same time over one Pilgrimage.</para>
/// </summary>
public sealed class AuspiceSession
{
    /// <summary>Logical stream for the Auspice rite (0 Conjunction, 1 peer exchange, 2 Consecration, 3 Epistles,
    /// 4 Conduits, 5 Oracle, 6 overlay control).</summary>
    public const ushort AuspiceStream = 7;

    private readonly IStreamChannel _channel;
    private readonly VeilCipher? _veil;
    private readonly AuspicePadding _padding;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public AuspiceSession(IStreamChannel channel, ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite, AuspicePadding? padding = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        ArgumentNullException.ThrowIfNull(suite);
        _veil = new VeilCipher(sessionKey, suite);
        _padding = padding ?? AuspicePadding.Default;
    }

    /// <summary>
    /// Binds over an <em>already-encrypted</em> transport (the Pilgrimage's Noise vessel): frames are sent with no
    /// Veil layer, because the vessel is itself the confidentiality + authenticity boundary. Padding still applies —
    /// encryption hides the bytes, not how many there are.
    /// </summary>
    public AuspiceSession(IStreamChannel channel, AuspicePadding? padding = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _veil = null;
        _padding = padding ?? AuspicePadding.Default;
    }

    /// <summary>Pilgrim: begin attending <paramref name="topic"/>. The Shrine answers with a snapshot, then updates.</summary>
    public Task AttendAsync(string topic, CancellationToken cancellationToken = default)
        => SendAsync(new AuspiceFrame { Kind = AuspiceFrameKind.Attend, Topic = topic }, cancellationToken);

    /// <summary>Pilgrim: stop attending <paramref name="topic"/>.</summary>
    public Task DepartAsync(string topic, CancellationToken cancellationToken = default)
        => SendAsync(new AuspiceFrame { Kind = AuspiceFrameKind.Depart, Topic = topic }, cancellationToken);

    /// <summary>Shrine: send the opening state of <paramref name="topic"/>.</summary>
    public Task SendSnapshotAsync(string topic, byte[] payload, CancellationToken cancellationToken = default)
        => SendAsync(new AuspiceFrame { Kind = AuspiceFrameKind.Snapshot, Topic = topic, Payload = payload }, cancellationToken);

    /// <summary>Shrine: send an incremental update for <paramref name="topic"/>.</summary>
    public Task SendUpdateAsync(string topic, byte[] payload, CancellationToken cancellationToken = default)
        => SendAsync(new AuspiceFrame { Kind = AuspiceFrameKind.Update, Topic = topic, Payload = payload }, cancellationToken);

    /// <summary>Shrine: tell the Pilgrim the feed has ended, with a short reason.</summary>
    public Task SendSealedAsync(string topic, string reason, CancellationToken cancellationToken = default)
        => SendAsync(new AuspiceFrame
        {
            Kind = AuspiceFrameKind.Sealed,
            Topic = topic,
            Payload = System.Text.Encoding.UTF8.GetBytes(reason ?? string.Empty),
        }, cancellationToken);

    /// <summary>Reads the next Auspice frame, or null when the peer closes the stream.</summary>
    public async Task<AuspiceFrame?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var payload = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (payload is null)
            return null;

        var reader = new CodexReader(payload);
        var body = reader.ReadBytes();
        var plaintext = _veil is null
            ? body.ToArray()
            : _veil.Open(body) ?? throw new EpistleException("Auspice content failed authentication (Veil).");

        return AuspiceCodec.Decode(plaintext);
    }

    /// <summary>
    /// Pilgrim convenience: attend <paramref name="topic"/> and yield its frames (the snapshot first, then updates)
    /// until the feed is sealed, the session closes, or the caller stops enumerating.
    /// </summary>
    public async IAsyncEnumerable<AuspiceFrame> AttendAllAsync(
        string topic,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await AttendAsync(topic, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var frame = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
                yield break;                                    // the session closed
            if (!string.Equals(frame.Topic, topic, StringComparison.Ordinal))
                continue;                                       // another feed shares this session
            if (frame.Kind == AuspiceFrameKind.Sealed)
                yield break;                                    // the Shrine ended this feed

            yield return frame;
        }
    }

    // Sends are serialised: a Shrine may emanate several feeds from independent tasks over the one stream.
    private async Task SendAsync(AuspiceFrame frame, CancellationToken cancellationToken)
    {
        var body = Pad(AuspiceCodec.Encode(frame));   // validates the topic and payload ceiling before any I/O
        var w = new CodexWriter();
        w.WriteBytes(_veil is null ? body : _veil.Seal(body));
        var wire = w.ToArray();

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _channel.SendAsync(wire, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Grows an encoded frame to its quantised length so an observer sees a bucket rather than the exact size. The
    /// padding sits inside what is subsequently sealed, and the decoder never reads it: the payload is
    /// length-prefixed, so trailing bytes are simply not part of any field.
    /// </summary>
    private byte[] Pad(byte[] body)
    {
        var target = _padding.Quantise(body.Length);
        if (target <= body.Length)
            return body;

        var padded = new byte[target];
        body.CopyTo(padded, 0);
        return padded;   // zero-filled tail; it is encrypted with the rest and never parsed
    }
}
