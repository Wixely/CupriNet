using System.Security.Cryptography;
using CupriNet.Alembic;

namespace CupriNet.Rites;

/// <summary>
/// A disk-backed Reliquary assembler: it verifies each chunk and writes it straight to a scratch file
/// rather than buffering the whole transfer in memory, then verifies the whole-file hash by streaming
/// before the file is finalized. Peak memory is a single chunk — so many concurrent transfers cannot
/// exhaust memory the way the in-memory <see cref="ReliquaryAssembler"/> can. Disposing without a
/// successful <see cref="CompleteTo"/> deletes the scratch file.
/// </summary>
public sealed class ReliquaryDiskAssembler : IDisposable
{
    private readonly ReliquaryFile _file;
    private readonly ICryptoSuite _suite;
    private readonly string _scratchPath;
    private readonly FileStream _stream;
    private readonly bool[] _received;
    private int _receivedCount;
    private bool _finalized;

    public ReliquaryDiskAssembler(ReliquaryFile file, ICryptoSuite suite, string scratchPath)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _suite = suite ?? throw new ArgumentNullException(nameof(suite));
        _scratchPath = scratchPath ?? throw new ArgumentNullException(nameof(scratchPath));
        _received = new bool[file.ChunkCount];
        _stream = new FileStream(scratchPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        if (file.Length > 0)
            _stream.SetLength(file.Length);
    }

    public int ChunkCount => _file.ChunkCount;

    public bool IsComplete => _receivedCount == ChunkCount;

    /// <summary>Verifies and writes a chunk to its offset. Returns false (rejecting) if index, length, or hash is wrong.</summary>
    public bool AcceptChunk(int index, ReadOnlySpan<byte> data)
    {
        if (index < 0 || index >= ChunkCount)
            return false;
        if (data.Length != ExpectedChunkLength(index))
            return false;
        if (!_suite.Hash.Sha256(data).AsSpan().SequenceEqual(_file.ChunkHashes[index]))
            return false;

        if (!_received[index])
        {
            _stream.Seek((long)index * _file.ChunkSize, SeekOrigin.Begin);
            _stream.Write(data);
            _received[index] = true;
            _receivedCount++;
        }

        return true;
    }

    /// <summary>Streams the scratch file to verify the whole-file hash, then atomically moves it to <paramref name="destinationPath"/>.</summary>
    public void CompleteTo(string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(destinationPath);
        if (!IsComplete)
            throw new ReliquaryException($"Cannot finalize: {ChunkCount - _receivedCount} chunk(s) still missing.");

        _stream.Flush();
        _stream.Seek(0, SeekOrigin.Begin);
        using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = _stream.Read(buffer, 0, buffer.Length)) > 0)
                hasher.AppendData(buffer, 0, read);
            if (!hasher.GetHashAndReset().AsSpan().SequenceEqual(_file.FullHash))
                throw new ReliquaryException("Assembled file failed whole-file hash verification.");
        }

        _stream.Dispose();
        _finalized = true;
        File.Move(_scratchPath, destinationPath, overwrite: true);
    }

    private int ExpectedChunkLength(int index)
    {
        if (index < ChunkCount - 1)
            return _file.ChunkSize;
        return (int)(_file.Length - (long)_file.ChunkSize * (ChunkCount - 1));
    }

    public void Dispose()
    {
        if (_finalized)
            return;
        try { _stream.Dispose(); } catch { /* best-effort cleanup */ }
        try { if (File.Exists(_scratchPath)) File.Delete(_scratchPath); } catch { /* best-effort cleanup */ }
    }
}
