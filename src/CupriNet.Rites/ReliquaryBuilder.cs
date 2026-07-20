using System.Security.Cryptography;
using CupriNet.Alembic;

namespace CupriNet.Rites;

/// <summary>Builds Reliquary manifests: chunks files, hashes each chunk and the whole file, applies Wards.</summary>
public static class ReliquaryBuilder
{
    /// <summary>Describes a single file: validates its path, then computes chunk and full hashes.</summary>
    public static ReliquaryFile DescribeFile(string relativePath, ReadOnlySpan<byte> content, int chunkSize, ICryptoSuite suite, ReliquaryLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(suite);
        limits ??= new ReliquaryLimits();

        var safePath = FilePathGuard.Normalize(relativePath, limits.MaxPathLength)
                       ?? throw new ReliquaryException($"Unsafe or invalid relative path: '{relativePath}'.");
        if (chunkSize <= 0 || chunkSize > limits.MaxChunkSize)
            throw new ReliquaryException($"Chunk size {chunkSize} is out of range (1..{limits.MaxChunkSize}).");

        var fullHash = suite.Hash.Sha256(content);
        var chunkHashes = new List<byte[]>();
        for (var offset = 0; offset < content.Length; offset += chunkSize)
        {
            var end = Math.Min(offset + chunkSize, content.Length);
            chunkHashes.Add(suite.Hash.Sha256(content[offset..end]));
        }

        return new ReliquaryFile
        {
            RelativePath = safePath,
            Length = content.Length,
            ChunkSize = chunkSize,
            FullHash = fullHash,
            ChunkHashes = chunkHashes,
        };
    }

    /// <summary>Builds a manifest for a set of files, enforcing file-count and total-size Wards.</summary>
    public static ReliquaryManifest Build(IReadOnlyList<(string Path, byte[] Content)> files, int chunkSize, ICryptoSuite suite, ReliquaryLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(suite);
        limits ??= new ReliquaryLimits();

        if (files.Count > limits.MaxFiles)
            throw new ReliquaryException($"Transfer has {files.Count} files, exceeding the maximum of {limits.MaxFiles}.");

        long total = 0;
        var described = new List<ReliquaryFile>(files.Count);
        foreach (var (path, content) in files)
        {
            total += content.Length;
            if (total > limits.MaxTotalBytes)
                throw new ReliquaryException($"Transfer exceeds the maximum total size of {limits.MaxTotalBytes} bytes.");
            described.Add(DescribeFile(path, content, chunkSize, suite, limits));
        }

        return new ReliquaryManifest
        {
            TransferId = RandomNumberGenerator.GetBytes(16),
            Files = described,
        };
    }
}
