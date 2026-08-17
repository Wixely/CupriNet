using System.Collections.Concurrent;

namespace CupriNet.Rites;

/// <summary>Sends the frames of one attended feed. Handed to an <see cref="IAuspiceSource"/> for the life of a subscription.</summary>
public interface IAuspicePublisher
{
    /// <summary>The feed being emanated.</summary>
    string Topic { get; }

    /// <summary>Sends the opening state. Send this once, before any update.</summary>
    Task SnapshotAsync(byte[] payload, CancellationToken cancellationToken = default);

    /// <summary>Sends an incremental update.</summary>
    Task UpdateAsync(byte[] payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// Produces a live feed for an attending Pilgrim — the streaming counterpart of <see cref="IOracleHandler"/>. The call
/// runs for the whole subscription: send a snapshot, then updates, until the token is cancelled (the Pilgrim departed
/// or the session ended). Returning ends the feed and seals it.
/// </summary>
public interface IAuspiceSource
{
    Task EmanateAsync(IAuspicePublisher publisher, CancellationToken cancellationToken = default);
}

/// <summary>An <see cref="IAuspiceSource"/> backed by a delegate.</summary>
public sealed class DelegateAuspiceSource(Func<IAuspicePublisher, CancellationToken, Task> source) : IAuspiceSource
{
    private readonly Func<IAuspicePublisher, CancellationToken, Task> _source =
        source ?? throw new ArgumentNullException(nameof(source));

    public Task EmanateAsync(IAuspicePublisher publisher, CancellationToken cancellationToken = default)
        => _source(publisher, cancellationToken);
}

/// <summary>
/// Runs named <see cref="IAuspiceSource"/>s over an <see cref="AuspiceSession"/>: reads attendance requests, starts a
/// source per feed, and stops it when the Pilgrim departs or the session ends. A source that throws seals only its own
/// feed — one bad feed never takes the Shrine down.
/// </summary>
public static class AuspiceHost
{
    /// <summary>Concurrent feeds one Pilgrim may attend, a Ward against a peer opening unbounded subscriptions.</summary>
    public const int MaxConcurrentFeeds = 32;

    public static async Task ServeAsync(
        AuspiceSession session,
        IReadOnlyDictionary<string, IAuspiceSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sources);

        var live = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var frame = await session.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                    return;                                     // the Pilgrim closed the session

                switch (frame.Kind)
                {
                    case AuspiceFrameKind.Attend:
                        await StartFeedAsync(session, sources, live, frame.Topic, cancellationToken).ConfigureAwait(false);
                        break;

                    case AuspiceFrameKind.Depart:
                        if (live.TryRemove(frame.Topic, out var stopping))
                            await CancelAsync(stopping).ConfigureAwait(false);
                        break;

                    default:
                        break;                                  // Shrine→Pilgrim kinds are ignored on this side
                }
            }
        }
        finally
        {
            foreach (var cts in live.Values)
                await CancelAsync(cts).ConfigureAwait(false);
        }
    }

    private static async Task StartFeedAsync(
        AuspiceSession session,
        IReadOnlyDictionary<string, IAuspiceSource> sources,
        ConcurrentDictionary<string, CancellationTokenSource> live,
        string topic,
        CancellationToken cancellationToken)
    {
        if (!sources.TryGetValue(topic, out var source))
        {
            await session.SendSealedAsync(topic, "no such feed", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (live.Count >= MaxConcurrentFeeds)
        {
            await session.SendSealedAsync(topic, "too many feeds", cancellationToken).ConfigureAwait(false);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!live.TryAdd(topic, cts))
        {
            cts.Dispose();                                       // already attending; ignore the duplicate
            return;
        }

        _ = Task.Run(async () =>
        {
            var reason = "ended";
            try
            {
                await source.EmanateAsync(new Publisher(session, topic), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;                                          // departed or shutting down: say nothing
            }
            catch
            {
                reason = "failed";                               // never surface internals to a Pilgrim
            }

            live.TryRemove(topic, out _);
            try { await session.SendSealedAsync(topic, reason, cts.Token).ConfigureAwait(false); }
            catch { /* the session is going away anyway */ }
            cts.Dispose();
        }, CancellationToken.None);
    }

    private static async Task CancelAsync(CancellationTokenSource cts)
    {
        try { await cts.CancelAsync().ConfigureAwait(false); } catch { /* already disposed */ }
    }

    private sealed class Publisher(AuspiceSession session, string topic) : IAuspicePublisher
    {
        public string Topic => topic;

        public Task SnapshotAsync(byte[] payload, CancellationToken cancellationToken = default)
            => session.SendSnapshotAsync(topic, payload, cancellationToken);

        public Task UpdateAsync(byte[] payload, CancellationToken cancellationToken = default)
            => session.SendUpdateAsync(topic, payload, cancellationToken);
    }
}
