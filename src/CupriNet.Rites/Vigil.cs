namespace CupriNet.Rites;

/// <summary>Tunable bounds for the <see cref="Vigil"/>.</summary>
public sealed record VigilOptions
{
    public int MaxPending { get; init; } = 1024;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxAttempts { get; init; } = 8;
}

/// <summary>The messages a sweep wants (re)sent now, and those given up on after too many attempts.</summary>
public readonly record struct VigilSweep(IReadOnlyList<Epistle> ToSend, IReadOnlyList<Epistle> Abandoned);

/// <summary>
/// The reliable-delivery layer: an outbound queue that redelivers unacknowledged Epistles with capped
/// exponential backoff and gives up after a bounded number of attempts. It is transport-agnostic and
/// clock-injected — the caller sweeps it (e.g. on a timer or on reconnect) and sends whatever is due,
/// then <see cref="Acknowledge"/>s each MessageId when an Attestation arrives. Redelivery is safe because
/// receivers dedup by MessageId (<see cref="EpistleDeduper"/>).
/// </summary>
public sealed class Vigil
{
    private sealed class Pending
    {
        public required Epistle Epistle { get; init; }
        public int Attempts { get; set; }
        public DateTimeOffset NextDue { get; set; }
    }

    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly VigilOptions _options;

    public Vigil(VigilOptions? options = null) => _options = options ?? new VigilOptions();

    public int PendingCount => _pending.Count;

    /// <summary>Queues an Epistle for delivery, due immediately. Returns false if the queue is full or a duplicate.</summary>
    public bool Enqueue(Epistle epistle, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(epistle);
        var key = Convert.ToHexStringLower(epistle.MessageId);
        if (_pending.ContainsKey(key))
            return false;
        if (_pending.Count >= _options.MaxPending)
            return false;

        _pending[key] = new Pending { Epistle = epistle, Attempts = 0, NextDue = now };
        return true;
    }

    /// <summary>Clears a pending Epistle once its Attestation (ack) has been received.</summary>
    public bool Acknowledge(ReadOnlySpan<byte> messageId)
        => _pending.Remove(Convert.ToHexStringLower(messageId));

    /// <summary>Returns the Epistles due for (re)send now, scheduling their next attempt, and abandons any past the attempt limit.</summary>
    public VigilSweep CollectDue(DateTimeOffset now)
    {
        var toSend = new List<Epistle>();
        var abandoned = new List<Epistle>();
        var toRemove = new List<string>();

        foreach (var (key, pending) in _pending)
        {
            if (pending.NextDue > now)
                continue;

            pending.Attempts++;
            if (pending.Attempts > _options.MaxAttempts)
            {
                abandoned.Add(pending.Epistle);
                toRemove.Add(key);
            }
            else
            {
                pending.NextDue = now + Backoff(pending.Attempts);
                toSend.Add(pending.Epistle);
            }
        }

        foreach (var key in toRemove)
            _pending.Remove(key);

        return new VigilSweep(toSend, abandoned);
    }

    private TimeSpan Backoff(int attempt)
    {
        var factor = Math.Pow(2, attempt - 1);
        var ticks = Math.Min((double)_options.MaxDelay.Ticks, _options.BaseDelay.Ticks * factor);
        return TimeSpan.FromTicks((long)ticks);
    }
}
