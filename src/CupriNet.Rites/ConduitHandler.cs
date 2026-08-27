namespace CupriNet.Rites;

/// <summary>
/// Serves the raw duplex rite for one visit — the streaming counterpart of <see cref="IOracleHandler"/> for a protocol
/// that is neither request/response nor snapshot-then-updates. The call runs for the whole conduit: read frames, send
/// frames, in whatever order the application's own protocol dictates, until the token is cancelled (the visit ended)
/// or the peer closes. Returning ends the conduit and seals it.
///
/// <para>An existing framed protocol moving onto L2 meets the seam here: the handler is handed a
/// <see cref="ConduitSession"/> whose <see cref="ConduitSession.SendAsync"/> / <see cref="ConduitSession.ReceiveAsync"/>
/// pair is the whole contract, so the protocol above it needs no CupriNet vocabulary.</para>
///
/// <para><b>On <see cref="ConduitFrame.ProtocolId"/>.</b> The rite never dispatches on it — there is no registry, and
/// the numbers are the application's own; CupriNet only frames and seals what it is given. A handler serving one
/// protocol should therefore check the id itself and answer an unrecognised one with
/// <see cref="ConduitSession.SendSealedAsync"/> ("unknown protocol") rather than ignoring the frame, so a peer that
/// dialled the wrong thing is told instead of left waiting. Doing it any other way is what makes two consumers behave
/// differently in the same situation.</para>
/// </summary>
public interface IConduitHandler
{
    Task AttendAsync(ConduitSession session, CancellationToken cancellationToken = default);
}

/// <summary>An <see cref="IConduitHandler"/> backed by a delegate.</summary>
public sealed class DelegateConduitHandler(Func<ConduitSession, CancellationToken, Task> handler) : IConduitHandler
{
    private readonly Func<ConduitSession, CancellationToken, Task> _handler =
        handler ?? throw new ArgumentNullException(nameof(handler));

    public Task AttendAsync(ConduitSession session, CancellationToken cancellationToken = default)
        => _handler(session, cancellationToken);
}

/// <summary>
/// Runs an <see cref="IConduitHandler"/> over a <see cref="ConduitSession"/> for the life of a visit. A handler that
/// throws seals only the conduit — one bad protocol never takes the Shrine down — and a Shrine that hosts none answers
/// the first frame with a seal rather than silence, so a peer is never left waiting on a stream nobody is reading.
/// </summary>
public static class ConduitHost
{
    public static async Task ServeAsync(
        ConduitSession session,
        IConduitHandler? handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (handler is null)
        {
            // Nothing hosted here. Wait for the peer to say something, then tell it why nothing will come back —
            // answering an unasked question would just be noise on a visit that never intended to use the conduit.
            var opener = await session.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (opener is null)
                return;

            await SealAsync(session, "no conduit hosted", cancellationToken).ConfigureAwait(false);
            await DrainAsync(session, cancellationToken).ConfigureAwait(false);
            return;
        }

        var reason = "ended";
        try
        {
            await handler.AttendAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;                                             // the visit is going away: say nothing
        }
        catch
        {
            reason = "failed";                                  // never surface internals to a Pilgrim
        }

        await SealAsync(session, reason, cancellationToken).ConfigureAwait(false);
        await DrainAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Holds the loop open after the conduit is sealed, until the peer actually goes away. The caller ends a visit
    /// when any rite's loop returns, so returning here the moment a conduit ends would take the Oracle, the Auspice
    /// and the Relic down with it — one bad rite must never take the Shrine down.
    /// </summary>
    private static async Task DrainAsync(ConduitSession session, CancellationToken cancellationToken)
    {
        while (await session.ReceiveAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            // Sealed already; the peer has been told. Anything further is discarded rather than answered.
        }
    }

    private static async Task SealAsync(ConduitSession session, string reason, CancellationToken cancellationToken)
    {
        try { await session.SendSealedAsync(reason, cancellationToken).ConfigureAwait(false); }
        catch { /* the session is going away anyway */ }
    }
}
