using System.Buffers.Binary;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// The reliable-UDP ARQ has no authentication of its own, so what a single stray or spoofed datagram can do to a
/// stream has to be bounded: it must not be able to acknowledge data that was never sent, and it must not open a
/// session on a listener unless it is at least shaped like a segment.
/// </summary>
public class ReliableArqHardeningTests
{
    private const byte CmdPush = 1;
    private const byte CmdAck = 2;

    private static byte[] Segment(byte cmd, uint seq, uint una, ReadOnlySpan<byte> payload = default)
    {
        var datagram = new byte[11 + payload.Length];
        datagram[0] = cmd;
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(1), seq);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(5), una);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(9), (ushort)payload.Length);
        payload.CopyTo(datagram.AsSpan(11));
        return datagram;
    }

    /// <summary>Drives two endpoints over a lossless in-memory wire until <paramref name="expected"/> bytes arrive at B.</summary>
    private static byte[] Deliver(ReliableArq a, ReliableArq b, Queue<byte[]> toA, Queue<byte[]> toB, int expected)
    {
        var received = new List<byte>(expected);
        for (long now = 0; now < 30_000 && received.Count < expected; now += 5)
        {
            while (toB.TryDequeue(out var d))
                b.Input(d);
            while (toA.TryDequeue(out var d))
                a.Input(d);
            a.Update(now);
            b.Update(now);
            while (b.Receive() is { } chunk)
                received.AddRange(chunk);
        }
        return [.. received];
    }

    [Fact]
    public void Ack_BeyondWhatWasSent_IsIgnored_AndTheStreamStillDelivers()
    {
        var toA = new Queue<byte[]>();
        var toB = new Queue<byte[]>();
        var a = new ReliableArq(d => toB.Enqueue(d.ToArray()));
        var b = new ReliableArq(d => toA.Enqueue(d.ToArray()));

        var payload = new byte[20_000];
        Random.Shared.NextBytes(payload);
        a.Send(payload);

        // One datagram claiming the peer has received a million segments A never sent.
        a.Input(Segment(CmdAck, seq: 0, una: 1_000_000));

        Assert.Equal(payload, Deliver(a, b, toA, toB, payload.Length));
    }

    [Fact]
    public void Ack_BeyondWhatWasSent_DoesNotDiscardInFlightSegments()
    {
        var sent = new List<byte[]>();
        var a = new ReliableArq(d => sent.Add(d.ToArray()));
        a.Send(new byte[3 * ReliableArq.Mss]);
        a.Update(0);
        Assert.Equal(3, a.InFlight);

        a.Input(Segment(CmdAck, seq: 0, una: 4)); // only 0..2 exist, so "next expected is 4" cannot be true

        Assert.Equal(3, a.InFlight);
    }

    [Fact]
    public void IsSegment_AcceptsWellFormedSegments_Only()
    {
        Assert.True(ReliableArq.IsSegment(Segment(CmdAck, 0, 0)));
        Assert.True(ReliableArq.IsSegment(Segment(CmdPush, 0, 0, [1, 2, 3])));

        var probe = new byte[33];
        probe[0] = 0xF1; // a hole-punch probe sharing the port
        Assert.False(ReliableArq.IsSegment(probe));

        Assert.False(ReliableArq.IsSegment(new byte[10])); // shorter than a header

        var truncated = Segment(CmdPush, 0, 0, [1, 2, 3])[..12]; // claims 3 payload bytes, carries 1
        Assert.False(ReliableArq.IsSegment(truncated));
    }
}
