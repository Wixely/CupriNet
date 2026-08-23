using CupriNet.Alembic;
using CupriNet.Vessel;

namespace CupriNet.Rites;

/// <summary>
/// Carries the Relic rite — chunked, hash-verified bulk content — over a stream channel. A Pilgrim
/// <see cref="FetchManifestAsync"/>es a named relic and <see cref="FetchFileAsync"/>es its files; a Shrine host runs
/// <see cref="RelicHost.ServeAsync"/> over the same session. Requests are strictly sequential (send one, read the
/// answer), so the transfer needs no correlation state — and because it runs on its own logical stream, a bulk fetch
/// never head-of-line-blocks the Oracle or the Auspice.
///
/// <para>Binds only over an <em>already-encrypted</em> transport (the Pilgrimage's Noise vessel): the vessel is the
/// confidentiality + authenticity boundary, so frames carry no Veil layer. No padding either — chunk frames are
/// naturally uniform (every one but the last is exactly the chunk size), and the volume of a bulk transfer is not
/// concealable by quantising message sizes.</para>
/// </summary>
public sealed class RelicSession
{
    /// <summary>Logical stream for the Relic rite (0 Conjunction, 1 peer exchange, 2 Consecration, 3 Epistles,
    /// 4 Conduits, 5 Oracle, 6 overlay control, 7 Auspice).</summary>
    public const ushort RelicStream = 8;

    /// <summary>
    /// The most a single fetched file may claim before <see cref="FetchFileAsync"/> refuses to allocate for it —
    /// a Ward against a hostile manifest ballooning the fetcher (in a browser tab, an OOM). Callers with a genuine
    /// larger relic raise the bound explicitly.
    /// </summary>
    public const long DefaultMaxFileBytes = 256L * 1024 * 1024;

    private readonly IStreamChannel _channel;
    private readonly ICryptoSuite _suite;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public RelicSession(IStreamChannel channel, ICryptoSuite suite)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _suite = suite ?? throw new ArgumentNullException(nameof(suite));
    }

    /// <summary>Sends one frame. Host side uses this through <see cref="RelicHost"/>.</summary>
    public async Task SendAsync(RelicFrame frame, CancellationToken cancellationToken = default)
    {
        var wire = RelicCodec.Encode(frame);   // validates the name and payload ceiling before any I/O
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

    /// <summary>Reads the next Relic frame, or null when the peer closes the stream.</summary>
    public async Task<RelicFrame?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var payload = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return payload is null ? null : RelicCodec.Decode(payload);
    }

    /// <summary>
    /// Pilgrim: fetches and validates the manifest of the named relic. Throws <see cref="RelicException"/> when the
    /// Shrine refuses (unknown relic), or when the manifest declares chunks the transport could never carry.
    /// </summary>
    public async Task<ReliquaryManifest> FetchManifestAsync(string name, CancellationToken cancellationToken = default)
    {
        var answer = await RequestAsync(
            new RelicFrame { Kind = RelicFrameKind.RequestManifest, Name = name },
            RelicFrameKind.Manifest, cancellationToken).ConfigureAwait(false);

        var manifest = ReliquaryCodec.Decode(answer.Payload);
        foreach (var file in manifest.Files)
        {
            // A chunk size past the frame ceiling describes chunks that can never arrive; refuse the manifest
            // here, with a reason, rather than failing later on a frame the codec rejects.
            if (file.ChunkSize > RelicCodec.MaxPayloadBytes)
                throw new RelicException(
                    $"Relic '{name}' declares {file.ChunkSize}-byte chunks, past the {RelicCodec.MaxPayloadBytes}-byte frame ceiling.");
        }

        return manifest;
    }

    /// <summary>
    /// Pilgrim: fetches one file of a fetched manifest, chunk by chunk, verifying each chunk's hash as it arrives and
    /// the whole-file hash before returning the bytes — so what comes back is exactly what the manifest promised, or
    /// a <see cref="RelicException"/>. For a resumable-to-disk transfer, drive <see cref="ReceiveAsync"/> against a
    /// <see cref="ReliquaryDiskAssembler"/> instead; the chunks re-requested on resume are just further requests.
    /// </summary>
    public async Task<byte[]> FetchFileAsync(
        string name,
        ReliquaryManifest manifest,
        int fileIndex,
        long maxBytes = DefaultMaxFileBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (fileIndex < 0 || fileIndex >= manifest.Files.Count)
            throw new ArgumentOutOfRangeException(nameof(fileIndex));

        var file = manifest.Files[fileIndex];
        if (file.Length > maxBytes)
            throw new RelicException(
                $"Relic '{name}' file '{file.RelativePath}' is {file.Length} bytes, past the {maxBytes}-byte bound. " +
                "Raise maxBytes if the size is expected.");

        var assembler = new ReliquaryAssembler(file, _suite);
        for (var chunk = 0; chunk < file.ChunkCount; chunk++)
        {
            var answer = await RequestAsync(
                new RelicFrame { Kind = RelicFrameKind.RequestChunk, Name = name, FileIndex = fileIndex, ChunkIndex = chunk },
                RelicFrameKind.Chunk, cancellationToken).ConfigureAwait(false);

            if (answer.FileIndex != fileIndex || answer.ChunkIndex != chunk)
                throw new RelicException(
                    $"Relic '{name}' answered chunk {answer.FileIndex}/{answer.ChunkIndex} when {fileIndex}/{chunk} was asked for.");
            if (!assembler.AcceptChunk(chunk, answer.Payload))
                throw new RelicException(
                    $"Relic '{name}' chunk {chunk} failed verification against the manifest (wrong length or hash).");
        }

        return assembler.Assemble();   // verifies the whole-file hash before handing anything back
    }

    // One sequential exchange: send the request, take the next frame for this relic as the answer. A Refused frame
    // becomes a typed exception carrying the Shrine's reason; an answer of the wrong kind is a protocol breach.
    private async Task<RelicFrame> RequestAsync(RelicFrame request, RelicFrameKind expected, CancellationToken cancellationToken)
    {
        await SendAsync(request, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var frame = await ReceiveAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new RelicException("Relic stream closed before an answer arrived.");
            if (!string.Equals(frame.Name, request.Name, StringComparison.Ordinal))
                continue;                                      // a stale answer for another relic; not ours
            if (frame.Kind == RelicFrameKind.Refused)
                throw new RelicException($"Shrine refused '{request.Name}': {frame.AsText()}");
            if (frame.Kind != expected)
                throw new RelicException($"Expected a {expected} frame for '{request.Name}', received {frame.Kind}.");

            return frame;
        }
    }
}
