namespace CupriNet.Rites;

/// <summary>
/// A bounded set of recently-seen MessageIds, making Epistle delivery idempotent: a redelivered message
/// is recognised and not delivered twice. Oldest ids are evicted once capacity is reached.
/// </summary>
public sealed class EpistleDeduper
{
    private readonly int _capacity;
    private readonly HashSet<string> _seen;
    private readonly Queue<string> _order;

    public EpistleDeduper(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _seen = new HashSet<string>(StringComparer.Ordinal);
        _order = new Queue<string>();
    }

    /// <summary>Marks a MessageId seen. Returns true if it was new (deliver it), false if a duplicate (drop it).</summary>
    public bool TryMarkSeen(ReadOnlySpan<byte> messageId)
    {
        var key = Convert.ToHexStringLower(messageId);
        if (!_seen.Add(key))
            return false;

        _order.Enqueue(key);
        if (_order.Count > _capacity)
            _seen.Remove(_order.Dequeue());

        return true;
    }
}
