using CupriNet.Codex;
using Xunit;

namespace CupriNet.UnitTests;

public class FrameCodecTests
{
    [Fact]
    public async Task Encode_Then_Read_RoundTrips()
    {
        byte[] payload = [10, 20, 30, 40, 50];
        var frame = FrameCodec.Encode(payload);

        using var stream = new MemoryStream(frame);
        var read = await FrameCodec.ReadAsync(stream);

        Assert.NotNull(read);
        Assert.True(read.SequenceEqual(payload));
    }

    [Fact]
    public async Task Read_TwoFrames_InOrder()
    {
        using var stream = new MemoryStream();
        var a = FrameCodec.Encode([1, 2, 3]);
        var b = FrameCodec.Encode([4, 5]);
        stream.Write(a);
        stream.Write(b);
        stream.Position = 0;

        var first = await FrameCodec.ReadAsync(stream);
        var second = await FrameCodec.ReadAsync(stream);
        var third = await FrameCodec.ReadAsync(stream);

        Assert.Equal(new byte[] { 1, 2, 3 }, first);
        Assert.Equal(new byte[] { 4, 5 }, second);
        Assert.Null(third); // clean end of stream
    }

    [Fact]
    public void Encode_OverMax_Throws()
    {
        var payload = new byte[64];
        Assert.Throws<CodexFormatException>(() => FrameCodec.Encode(payload, maxFrameSize: 32));
    }

    [Fact]
    public async Task Read_DeclaredLengthOverMax_Throws()
    {
        // Length prefix says 1000 bytes but caller caps at 16.
        var frame = FrameCodec.Encode(new byte[1000]);
        using var stream = new MemoryStream(frame);
        await Assert.ThrowsAsync<CodexFormatException>(
            async () => await FrameCodec.ReadAsync(stream, maxFrameSize: 16));
    }
}
