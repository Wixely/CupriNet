using CupriNet.Rites;
using Xunit;

namespace CupriNet.UnitTests;

public class ReliquaryTests
{
    private static byte[] Bytes(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++)
            b[i] = (byte)(i * 31 + 7);
        return b;
    }

    [Fact]
    public void Manifest_Build_And_Codec_RoundTrip()
    {
        var suite = CryptoSuites.Secure();
        var manifest = ReliquaryBuilder.Build(
            [("a.bin", Bytes(1000)), ("docs/b.txt", Bytes(50))],
            chunkSize: 256, suite);

        var decoded = ReliquaryCodec.Decode(ReliquaryCodec.Encode(manifest));

        Assert.Equal(manifest.TransferId, decoded.TransferId);
        Assert.Equal(2, decoded.Files.Count);
        Assert.Equal("a.bin", decoded.Files[0].RelativePath);
        Assert.Equal(1000, decoded.Files[0].Length);
        Assert.Equal(4, decoded.Files[0].ChunkCount); // ceil(1000/256)
        Assert.Equal(manifest.Files[0].FullHash, decoded.Files[0].FullHash);
    }

    [Fact]
    public void Builder_RejectsUnsafePath()
    {
        var suite = CryptoSuites.Secure();
        Assert.Throws<ReliquaryException>(() =>
            ReliquaryBuilder.DescribeFile("../escape", Bytes(10), 256, suite));
    }

    [Fact]
    public void Assembler_VerifiesChunks_SupportsResume_AndFullHash()
    {
        var suite = CryptoSuites.Secure();
        var content = Bytes(1000);
        var file = ReliquaryBuilder.DescribeFile("a.bin", content, chunkSize: 256, suite);
        var assembler = new ReliquaryAssembler(file, suite);

        Assert.Equal(4, assembler.ChunkCount);
        Assert.Equal(new[] { 0, 1, 2, 3 }, assembler.MissingChunks());

        // Deliver chunks out of order, leaving one for "resume".
        for (var i = 0; i < file.ChunkCount; i++)
        {
            if (i == 2)
                continue;
            var start = i * file.ChunkSize;
            var len = Math.Min(file.ChunkSize, content.Length - start);
            Assert.True(assembler.AcceptChunk(i, content.AsSpan(start, len)));
        }

        Assert.False(assembler.IsComplete);
        Assert.Equal(new[] { 2 }, assembler.MissingChunks()); // resume set

        // A corrupt chunk is rejected...
        Assert.False(assembler.AcceptChunk(2, new byte[file.ChunkSize])); // wrong content
        Assert.False(assembler.IsComplete);

        // ...the correct chunk completes and verifies.
        var s2 = 2 * file.ChunkSize;
        Assert.True(assembler.AcceptChunk(2, content.AsSpan(s2, Math.Min(file.ChunkSize, content.Length - s2))));
        Assert.True(assembler.IsComplete);
        Assert.Equal(content, assembler.Assemble());
    }

    [Fact]
    public void DiskAssembler_StreamsChunksToDisk_VerifiesAndFinalizes()
    {
        var suite = CryptoSuites.Secure();
        var content = Bytes(1000);
        var file = ReliquaryBuilder.DescribeFile("a.bin", content, chunkSize: 256, suite);
        var scratch = Path.Combine(Path.GetTempPath(), "cupri-scratch-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(Path.GetTempPath(), "cupri-dest-" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var assembler = new ReliquaryDiskAssembler(file, suite, scratch))
            {
                // Out of order, with a rejected corrupt chunk.
                Assert.False(assembler.AcceptChunk(2, new byte[file.ChunkSize]));
                for (var i = file.ChunkCount - 1; i >= 0; i--)
                {
                    var start = i * file.ChunkSize;
                    var len = Math.Min(file.ChunkSize, content.Length - start);
                    Assert.True(assembler.AcceptChunk(i, content.AsSpan(start, len)));
                }

                Assert.True(assembler.IsComplete);
                Assert.False(File.Exists(dest));
                assembler.CompleteTo(dest);
            }

            Assert.Equal(content, File.ReadAllBytes(dest));
            Assert.False(File.Exists(scratch)); // scratch moved, not left behind
        }
        finally
        {
            if (File.Exists(scratch)) File.Delete(scratch);
            if (File.Exists(dest)) File.Delete(dest);
        }
    }

    [Fact]
    public void DiskAssembler_Dispose_WithoutFinalize_DeletesScratch()
    {
        var suite = CryptoSuites.Secure();
        var file = ReliquaryBuilder.DescribeFile("a.bin", Bytes(300), chunkSize: 256, suite);
        var scratch = Path.Combine(Path.GetTempPath(), "cupri-scratch-" + Guid.NewGuid().ToString("N"));

        var assembler = new ReliquaryDiskAssembler(file, suite, scratch);
        assembler.AcceptChunk(0, Bytes(300).AsSpan(0, 256));
        Assert.True(File.Exists(scratch));
        assembler.Dispose();
        Assert.False(File.Exists(scratch)); // incomplete transfer cleaned up
    }

    [Fact]
    public void Assembler_HandlesEmptyFile()
    {
        var suite = CryptoSuites.Secure();
        var file = ReliquaryBuilder.DescribeFile("empty.bin", ReadOnlySpan<byte>.Empty, 256, suite);
        var assembler = new ReliquaryAssembler(file, suite);

        Assert.Equal(0, assembler.ChunkCount);
        Assert.True(assembler.IsComplete);
        Assert.Empty(assembler.Assemble());
    }

    [Fact]
    public async Task Writer_WritesUnderRoot_AndRejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "cuprinet-reliquary-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ReliquaryWriter.WriteAsync(root, "docs/report.txt", "hello"u8.ToArray());
            var written = await File.ReadAllTextAsync(Path.Combine(root, "docs", "report.txt"));
            Assert.Equal("hello", written);

            await Assert.ThrowsAsync<ReliquaryException>(
                async () => await ReliquaryWriter.WriteAsync(root, "../escape.txt", "x"u8.ToArray()));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
