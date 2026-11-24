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

    private static byte[] Segment(byte cmd, uint seq, uint una, ReadOnlySpan<byte> payload = default, ulong connection = 0)
    {
        var datagram = new byte[19 + payload.Length];
        datagram[0] = cmd;
        BinaryPrimitives.WriteUInt64BigEndian(datagram.AsSpan(1), connection);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(9), seq);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(13), una);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(17), (ushort)payload.Length);
        payload.CopyTo(datagram.AsSpan(19));
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

        Assert.False(ReliableArq.IsSegment(new byte[18])); // shorter than a header

        var truncated = Segment(CmdPush, 0, 0, [1, 2, 3])[..20]; // claims 3 payload bytes, carries 1
        Assert.False(ReliableArq.IsSegment(truncated));
    }

    [Fact]
    public void ASegment_ForAnotherConnection_IsIgnoredEntirely()
    {
        var sent = new List<byte[]>();
        var b = new ReliableArq(d => sent.Add(d.ToArray()), connectionId: 0x1122334455667788);

        // Right address, right sequence, wrong connection: what a host that is not on the path can manage.
        b.Input(Segment(CmdPush, seq: 0, una: 0, [9, 9, 9], connection: 0x1122334455667789));
        b.Update(0);

        Assert.Null(b.Receive());
        Assert.Empty(sent); // not even an ack — nothing tells the sender it guessed anything

        b.Input(Segment(CmdPush, seq: 0, una: 0, [1, 2, 3], connection: 0x1122334455667788));
        Assert.Equal(new byte[] { 1, 2, 3 }, b.Receive());
    }

    [Fact]
    public void AnAcceptingEnd_AdoptsTheFirstConnectionIdItSees_AndHoldsToIt()
    {
        var sent = new List<byte[]>();
        var b = new ReliableArq(d => sent.Add(d.ToArray()), connectionId: null);

        b.Send([7]);
        b.Update(0);
        Assert.Empty(sent); // it cannot speak before it knows which connection this is

        b.Input(Segment(CmdPush, seq: 0, una: 0, [1], connection: 42));
        Assert.Equal(42UL, b.ConnectionId);
        b.Update(1);
        Assert.NotEmpty(sent);
        Assert.All(sent, d => Assert.Equal(42UL, BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(1))));

        b.Input(Segment(CmdPush, seq: 1, una: 0, [2], connection: 43));
        Assert.Equal(new byte[] { 1 }, b.Receive());
        Assert.Null(b.Receive());
    }

    [Fact]
    public void APeer_ThatNeverAcknowledges_IsDeclaredDead_NotRetriedForever()
    {
        var sent = 0;
        var a = new ReliableArq(_ => sent++, maxRetransmits: 4, idleTimeoutMs: int.MaxValue);
        a.Send([1]);

        for (long now = 0; now < 120_000 && !a.Dead; now += 10)
            a.Update(now);

        Assert.True(a.Dead);
        Assert.Equal(5, sent); // the first send and four retries, then it stops
    }

    [Fact]
    public void AStream_ThatHearsNothing_IsDeclaredDead_ButAQuietLivePairIsNot()
    {
        var alone = new ReliableArq(_ => { }, idleTimeoutMs: 5_000, keepAliveMs: 1_000);
        for (long now = 0; now <= 6_000; now += 100)
            alone.Update(now);
        Assert.True(alone.Dead);

        // Two ends with nothing to say to each other keep each other alive with bare acks.
        var toA = new Queue<byte[]>();
        var toB = new Queue<byte[]>();
        var a = new ReliableArq(d => toB.Enqueue(d.ToArray()), idleTimeoutMs: 5_000, keepAliveMs: 1_000);
        var b = new ReliableArq(d => toA.Enqueue(d.ToArray()), idleTimeoutMs: 5_000, keepAliveMs: 1_000);
        for (long now = 0; now <= 60_000; now += 100)
        {
            while (toB.TryDequeue(out var d)) b.Input(d);
            while (toA.TryDequeue(out var d)) a.Input(d);
            a.Update(now);
            b.Update(now);
        }
        Assert.False(a.Dead);
        Assert.False(b.Dead);
    }

    [Fact]
    public void Close_IsRetransmittedUntilAcknowledged()
    {
        var toA = new Queue<byte[]>();
        var toB = new Queue<byte[]>();
        var dropped = 0;
        var a = new ReliableArq(d =>
        {
            if (dropped++ < 3)
                return; // the first three datagrams — every early copy of the CLOSE — are lost
            toB.Enqueue(d.ToArray());
        });
        var b = new ReliableArq(d => toA.Enqueue(d.ToArray()));

        a.Close();
        for (long now = 0; now < 30_000 && !a.CloseAcknowledged; now += 5)
        {
            while (toB.TryDequeue(out var d)) b.Input(d);
            while (toA.TryDequeue(out var d)) a.Input(d);
            a.Update(now);
            b.Update(now);
        }

        Assert.True(a.CloseAcknowledged);
        Assert.True(b.PeerFinished);
    }
}
