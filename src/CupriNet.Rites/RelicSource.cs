using CupriNet.Alembic;

namespace CupriNet.Rites;

/// <summary>
/// Resolves the relics a Shrine keeps — the bulk-content seam beside <see cref="IOracleHandler"/> (pages) and
/// <see cref="IAuspiceSource"/> (live feeds). A source answers two questions, statelessly: what is the manifest of
/// the named relic, and what are the bytes of one chunk of one of its files. The Pilgrim verifies everything against
/// the manifest, so a source that answers inconsistently only ever fails a fetch — it cannot corrupt one.
/// </summary>
public interface IRelicSource
{
    /// <summary>The manifest of the named relic, or null when the Shrine keeps no such relic.</summary>
    ValueTask<ReliquaryManifest?> ResolveAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>One chunk of one file of the named relic, or null when the relic, file, or chunk does not exist.</summary>
    ValueTask<byte[]?> ReadChunkAsync(string name, int fileIndex, int chunkIndex, CancellationToken cancellationToken = default);
}

/// <summary>
/// An <see cref="IRelicSource"/> over in-memory content: name each relic once, before hosting, and serve forever.
/// Manifests are built (and their frame-ceiling fit proven) at <see cref="Add(string, byte[], int)"/> time, so a
/// relic that cannot be served is the author's error at composition, never a Pilgrim's failed fetch at run time.
/// </summary>
public sealed class StaticRelicSource : IRelicSource
{
    private readonly Dictionary<string, (ReliquaryManifest Manifest, IReadOnlyList<byte[]> Contents)> _relics = new(StringComparer.Ordinal);
    private readonly ICryptoSuite _suite;

    public StaticRelicSource(ICryptoSuite suite)
        => _suite = suite ?? throw new ArgumentNullException(nameof(suite));

    /// <summary>Names a single-file relic. The name is a guarded relative path and doubles as the file's path.</summary>
    public StaticRelicSource Add(string name, byte[] content, int chunkSize = RelicCodec.DefaultChunkBytes)
        => Add(name, [(name, content)], chunkSize);

    /// <summary>Names a multi-file relic (a bundle) — one manifest, fetched file by file.</summary>
    public StaticRelicSource Add(string name, IReadOnlyList<(string Path, byte[] Content)> files, int chunkSize = RelicCodec.DefaultChunkBytes)
    {
        ArgumentNullException.ThrowIfNull(files);
        var safe = FilePathGuard.Normalize(name, RelicCodec.MaxNameLength)
                   ?? throw new RelicException($"Unsafe or invalid relic name: '{name}'.");
        if (chunkSize is < 1 or > RelicCodec.MaxPayloadBytes)
            throw new RelicException($"Relic chunk size {chunkSize} is out of range (1..{RelicCodec.MaxPayloadBytes}).");

        var manifest = ReliquaryBuilder.Build(files.ToList(), chunkSize, _suite);

        // Prove the manifest travels: it must fit one frame today, at this chunking — not when a Pilgrim asks.
        var encoded = ReliquaryCodec.Encode(manifest);
        if (encoded.Length > RelicCodec.MaxPayloadBytes)
            throw new RelicException(
                $"Relic '{safe}' has a {encoded.Length}-byte manifest, past the {RelicCodec.MaxPayloadBytes}-byte frame " +
                "ceiling. Raise the chunk size or shrink the relic.");

        _relics[safe] = (manifest, files.Select(f => f.Content).ToList());
        return this;
    }

    public ValueTask<ReliquaryManifest?> ResolveAsync(string name, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_relics.TryGetValue(name, out var relic) ? relic.Manifest : null);

    public ValueTask<byte[]?> ReadChunkAsync(string name, int fileIndex, int chunkIndex, CancellationToken cancellationToken = default)
    {
        if (!_relics.TryGetValue(name, out var relic))
            return ValueTask.FromResult<byte[]?>(null);
        if (fileIndex < 0 || fileIndex >= relic.Manifest.Files.Count)
            return ValueTask.FromResult<byte[]?>(null);

        var file = relic.Manifest.Files[fileIndex];
        if (chunkIndex < 0 || chunkIndex >= file.ChunkCount)
            return ValueTask.FromResult<byte[]?>(null);

        var content = relic.Contents[fileIndex];
        var offset = (long)file.ChunkSize * chunkIndex;
        var length = (int)Math.Min(file.ChunkSize, content.Length - offset);
        return ValueTask.FromResult<byte[]?>(content.AsSpan((int)offset, length).ToArray());
    }
}

/// <summary>
/// Runs an <see cref="IRelicSource"/> over a <see cref="RelicSession"/>: reads requests and answers them, in order,
/// until the Pilgrim closes the session. Answers stay in request order because the Pilgrim's exchange is strictly
/// sequential. Every failure is a <see cref="RelicFrameKind.Refused"/> frame with a short reason — a bad request
/// must neither take the Shrine down nor leak its internals.
/// </summary>
public static class RelicHost
{
    public static async Task ServeAsync(RelicSession session, IRelicSource? source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frame = await session.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
                return;                                        // the Pilgrim closed the session

            var answer = frame.Kind switch
            {
                RelicFrameKind.RequestManifest => await AnswerManifestAsync(source, frame, cancellationToken).ConfigureAwait(false),
                RelicFrameKind.RequestChunk => await AnswerChunkAsync(source, frame, cancellationToken).ConfigureAwait(false),
                _ => null,                                     // Shrine→Pilgrim kinds are ignored on this side
            };

            if (answer is not null)
                await session.SendAsync(answer, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<RelicFrame> AnswerManifestAsync(IRelicSource? source, RelicFrame request, CancellationToken cancellationToken)
    {
        try
        {
            var manifest = source is null
                ? null
                : await source.ResolveAsync(request.Name, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
                return Refusal(request, "no such relic");

            var encoded = ReliquaryCodec.Encode(manifest);
            return encoded.Length > RelicCodec.MaxPayloadBytes
                ? Refusal(request, "manifest too large")       // a source bypassing StaticRelicSource's proof
                : new RelicFrame { Kind = RelicFrameKind.Manifest, Name = request.Name, Payload = encoded };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Refusal(request, "failed");                 // never surface internals to a Pilgrim
        }
    }

    private static async Task<RelicFrame> AnswerChunkAsync(IRelicSource? source, RelicFrame request, CancellationToken cancellationToken)
    {
        try
        {
            var chunk = source is null
                ? null
                : await source.ReadChunkAsync(request.Name, request.FileIndex, request.ChunkIndex, cancellationToken).ConfigureAwait(false);
            if (chunk is null)
                return Refusal(request, "no such chunk");

            return chunk.Length > RelicCodec.MaxPayloadBytes
                ? Refusal(request, "chunk too large")          // a source cut against the frame ceiling
                : new RelicFrame
                {
                    Kind = RelicFrameKind.Chunk,
                    Name = request.Name,
                    FileIndex = request.FileIndex,
                    ChunkIndex = request.ChunkIndex,
                    Payload = chunk,
                };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Refusal(request, "failed");                 // never surface internals to a Pilgrim
        }
    }

    private static RelicFrame Refusal(RelicFrame request, string reason) => new()
    {
        Kind = RelicFrameKind.Refused,
        Name = request.Name,
        FileIndex = request.FileIndex,
        ChunkIndex = request.ChunkIndex,
        Payload = System.Text.Encoding.UTF8.GetBytes(reason),
    };
}
