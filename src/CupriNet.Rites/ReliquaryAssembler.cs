using CupriNet.Alembic;

namespace CupriNet.Rites;

/// <summary>
/// Receives and verifies the chunks of one file against its manifest entry. Each chunk is checked for
/// length and hash before being accepted, so corrupt or malicious chunks are rejected. Supports
/// resumption (it reports which chunks are still missing) and verifies the whole-file hash before the
/// assembled bytes are ever handed back.
/// </summary>
public sealed class ReliquaryAssembler
{
    private readonly ReliquaryFile _file;
    private readonly ICryptoSuite _suite;
    private readonly byte[]?[] _chunks;
    private int _received;

    public ReliquaryAssembler(ReliquaryFile file, ICryptoSuite suite)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _suite = suite ?? throw new ArgumentNullException(nameof(suite));
        _chunks = new byte[file.ChunkCount][];
    }

    public int ChunkCount => _file.ChunkCount;

    public bool IsComplete => _received == ChunkCount;

    /// <summary>Chunk indices not yet accepted — the set to (re)request when resuming.</summary>
    public IReadOnlyList<int> MissingChunks()
    {
        var missing = new List<int>();
        for (var i = 0; i < _chunks.Length; i++)
        {
            if (_chunks[i] is null)
                missing.Add(i);
        }

        return missing;
    }

    /// <summary>Verifies and stores a chunk. Returns false (rejecting it) if the index, length, or hash is wrong.</summary>
    public bool AcceptChunk(int index, ReadOnlySpan<byte> data)
    {
        if (index < 0 || index >= ChunkCount)
            return false;
        if (data.Length != ExpectedChunkLength(index))
            return false;
        if (!_suite.Hash.Sha256(data).AsSpan().SequenceEqual(_file.ChunkHashes[index]))
            return false;

        if (_chunks[index] is null)
        {
            _chunks[index] = data.ToArray();
            _received++;
        }

        return true;
    }

    /// <summary>Concatenates the chunks and verifies the whole-file hash. Throws unless complete and intact.</summary>
    public byte[] Assemble()
    {
        if (!IsComplete)
            throw new ReliquaryException($"Cannot assemble: {MissingChunks().Count} chunk(s) still missing.");

        var buffer = new byte[_file.Length];
        var offset = 0;
        foreach (var chunk in _chunks)
        {
            chunk!.CopyTo(buffer, offset);
            offset += chunk.Length;
        }

        if (!_suite.Hash.Sha256(buffer).AsSpan().SequenceEqual(_file.FullHash))
            throw new ReliquaryException("Assembled file failed whole-file hash verification.");

        return buffer;
    }

    private int ExpectedChunkLength(int index)
    {
        if (index < ChunkCount - 1)
            return _file.ChunkSize;
        // Last chunk holds the remainder.
        return (int)(_file.Length - (long)_file.ChunkSize * (ChunkCount - 1));
    }
}
