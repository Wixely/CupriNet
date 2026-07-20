using CupriNet.Codex;
using Xunit;

namespace CupriNet.UnitTests;

public class CodexTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(300UL)]
    [InlineData(ulong.MaxValue)]
    public void VarUInt_RoundTrips(ulong value)
    {
        var writer = new CodexWriter();
        writer.WriteVarUInt(value);

        var reader = new CodexReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadVarUInt());
        Assert.True(reader.End);
    }

    [Fact]
    public void MixedFields_RoundTrip_InOrder()
    {
        var writer = new CodexWriter();
        writer.WriteByte(7);
        writer.WriteUInt32(0xDEADBEEF);
        writer.WriteUInt64(0x0123456789ABCDEF);
        writer.WriteString("Dungeons & Dragons");
        writer.WriteBytes([1, 2, 3, 4]);

        var reader = new CodexReader(writer.ToArray());
        Assert.Equal(7, reader.ReadByte());
        Assert.Equal(0xDEADBEEFu, reader.ReadUInt32());
        Assert.Equal(0x0123456789ABCDEFul, reader.ReadUInt64());
        Assert.Equal("Dungeons & Dragons", reader.ReadString());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, reader.ReadBytes().ToArray());
        Assert.True(reader.End);
    }

    [Fact]
    public void ReadBytes_BeyondBuffer_Throws()
    {
        // A varint length of 10 followed by only 2 bytes of content.
        byte[] malformed = [10, 0xAA, 0xBB];
        Assert.Throws<CodexFormatException>(() =>
        {
            var reader = new CodexReader(malformed);
            reader.ReadBytes();
        });
    }

    [Fact]
    public void Encoding_IsDeterministic()
    {
        static byte[] Encode()
        {
            var w = new CodexWriter();
            w.WriteString("channel");
            w.WriteVarUInt(42);
            return w.ToArray();
        }

        Assert.True(Encode().SequenceEqual(Encode()));
    }
}
