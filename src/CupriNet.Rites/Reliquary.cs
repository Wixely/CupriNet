using CupriNet.Codex;

namespace CupriNet.Rites;

/// <summary>Thrown when a Reliquary transfer is malformed, unsafe, or fails verification.</summary>
public sealed class ReliquaryException(string message) : Exception(message);

/// <summary>Bounds (Wards) applied to a Reliquary transfer.</summary>
public sealed record ReliquaryLimits
{
    public int MaxFiles { get; init; } = 1024;
    public long MaxTotalBytes { get; init; } = 8L * 1024 * 1024 * 1024; // 8 GiB
    public int MaxPathLength { get; init; } = 1024;
    public int MaxChunkSize { get; init; } = 8 * 1024 * 1024;          // 8 MiB
    public int DefaultChunkSize { get; init; } = 64 * 1024;            // 64 KiB
    public int MaxChunksPerFile { get; init; } = 1 << 20;              // ~1M chunks (a Ward)
}

/// <summary>One file within a transfer: its relative path, size, chunking, and integrity hashes.</summary>
public sealed record ReliquaryFile
{
    public required string RelativePath { get; init; }
    public required long Length { get; init; }
    public required int ChunkSize { get; init; }
    public required byte[] FullHash { get; init; }
    public required IReadOnlyList<byte[]> ChunkHashes { get; init; }

    /// <summary>Number of chunks the file is divided into.</summary>
    public int ChunkCount => ChunkHashes.Count;
}

/// <summary>A signed-free manifest describing a multi-file transfer. Receivers accept it explicitly.</summary>
public sealed record ReliquaryManifest
{
    public required byte[] TransferId { get; init; }
    public required IReadOnlyList<ReliquaryFile> Files { get; init; }

    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (var file in Files)
                total += file.Length;
            return total;
        }
    }
}

/// <summary>Canonical serialization for <see cref="ReliquaryManifest"/>.</summary>
public static class ReliquaryCodec
{
    public static byte[] Encode(ReliquaryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var w = new CodexWriter();
        w.WriteBytes(manifest.TransferId);
        w.WriteVarUInt((ulong)manifest.Files.Count);
        foreach (var file in manifest.Files)
        {
            w.WriteString(file.RelativePath);
            w.WriteUInt64((ulong)file.Length);
            w.WriteVarUInt((ulong)file.ChunkSize);
            w.WriteBytes(file.FullHash);
            w.WriteVarUInt((ulong)file.ChunkHashes.Count);
            foreach (var hash in file.ChunkHashes)
                w.WriteBytes(hash);
        }

        return w.ToArray();
    }

    public static ReliquaryManifest Decode(ReadOnlySpan<byte> data, ReliquaryLimits? limits = null)
    {
        limits ??= new ReliquaryLimits();
        var r = new CodexReader(data);
        var transferId = r.ReadBytes().ToArray();

        var fileCount = r.ReadVarUInt();
        if (fileCount > (ulong)limits.MaxFiles)
            throw new CodexFormatException($"Manifest has {fileCount} files, exceeding the maximum of {limits.MaxFiles}.");

        var files = new List<ReliquaryFile>((int)fileCount);
        for (var i = 0UL; i < fileCount; i++)
        {
            var path = r.ReadString();
            var length = (long)r.ReadUInt64();
            var chunkSize = (int)r.ReadVarUInt();
            var fullHash = r.ReadBytes().ToArray();
            var hashCount = r.ReadVarUInt();
            if (hashCount > (ulong)limits.MaxChunksPerFile)
                throw new CodexFormatException($"File declares {hashCount} chunks, exceeding the maximum of {limits.MaxChunksPerFile}.");
            var chunkHashes = new List<byte[]>();
            for (var h = 0UL; h < hashCount; h++)
                chunkHashes.Add(r.ReadBytes().ToArray());

            files.Add(new ReliquaryFile
            {
                RelativePath = path,
                Length = length,
                ChunkSize = chunkSize,
                FullHash = fullHash,
                ChunkHashes = chunkHashes,
            });
        }

        return new ReliquaryManifest { TransferId = transferId, Files = files };
    }
}
