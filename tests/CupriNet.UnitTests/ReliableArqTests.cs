using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// The reliable-UDP ARQ (the "KCP route"): proves that application data is delivered completely and in order over
/// a channel that drops, reorders, and duplicates datagrams — the guarantee a Noise/mux session needs to run over
/// a hole-punched UDP path. Driven deterministically over a seeded in-memory lossy channel and a virtual clock.
/// </summary>
public class ReliableArqTests
{
    /// <summary>A seeded, lossy, reordering, duplicating in-memory channel between two ARQ endpoints.</summary>
    private sealed class Harness
    {
        private readonly Random _rng;
        private readonly double _drop;
        private readonly double _dup;
        private readonly int _delayJitterMs;
        private readonly List<(long At, bool ToB, byte[] Data)> _wire = [];

        public ReliableArq A { get; }
        public ReliableArq B { get; }

        public Harness(int seed, double drop, double dup, int delayJitterMs)
        {
            _rng = new Random(seed);
            _drop = drop;
            _dup = dup;
            _delayJitterMs = delayJitterMs;
            A = new ReliableArq(d => Schedule(toB: true, d));
            B = new ReliableArq(d => Schedule(toB: false, d));
        }

        private void Schedule(bool toB, ReadOnlyMemory<byte> datagram)
        {
            void Once()
            {
                if (_rng.NextDouble() < _drop)
                    return; // dropped — the ARQ must recover via retransmission
                var at = _now + 1 + _rng.Next(_delayJitterMs + 1); // reordered by a random small delay
                _wire.Add((at, toB, datagram.ToArray()));
            }
            Once();
            if (_rng.NextDouble() < _dup)
                Once(); // duplicated
        }

        private long _now;

        /// <summary>Runs the exchange until B has received <paramref name="expected"/> bytes (or the deadline). Returns them.</summary>
        public byte[] Run(int expected, long deadlineMs = 120_000)
        {
            var received = new List<byte>(expected);
            for (_now = 0; _now < deadlineMs && received.Count < expected; _now += 5)
            {
                foreach (var (_, toB, data) in _wire.Where(p => p.At <= _now).ToList())
                    (toB ? B : A).Input(data);
                _wire.RemoveAll(p => p.At <= _now);

                A.Update(_now);
                B.Update(_now);

                while (B.Receive() is { } chunk)
                    received.AddRange(chunk);
            }
            return [.. received];
        }
    }

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
            data[i] = (byte)((i * 31 + 7) & 0xFF); // a position-dependent pattern: any reorder/gap/dup corrupts it
        return data;
    }

    [Theory]
    [InlineData(1, 0.0, 0.0, 0)]   // clean channel
    [InlineData(2, 0.3, 0.1, 3)]   // 30% loss, 10% dup, reordering
    [InlineData(7, 0.5, 0.2, 6)]   // brutal: half the datagrams dropped, heavy dup + reorder
    public void DeliversCompleteAndInOrder_OverLossyReorderingChannel(int seed, double drop, double dup, int jitter)
    {
        var payload = Pattern(64 * 1024); // ~58 segments
        var harness = new Harness(seed, drop, dup, jitter);
        harness.A.Send(payload);

        var received = harness.Run(payload.Length);

        Assert.Equal(payload.Length, received.Length);
        Assert.Equal(payload, received); // exact bytes, exact order
    }

    [Fact]
    public void Close_IsDeliveredInOrder_AfterAllData_AsEndOfStream()
    {
        var payload = Pattern(20_000);
        var harness = new Harness(seed: 3, drop: 0.25, dup: 0.05, delayJitterMs: 4);
        harness.A.Send(payload);
        harness.A.Close();

        var received = harness.Run(payload.Length);
        Assert.Equal(payload, received);

        // Drive a little longer so the CLOSE marker propagates and gets acked, then assert EOF is observed.
        harness.Run(expected: payload.Length + 1, deadlineMs: 10_000);
        Assert.True(harness.B.PeerFinished, "peer end-of-stream should be delivered in order after all data");
    }
}
