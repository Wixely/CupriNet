namespace CupriNet.Rites;

/// <summary>
/// Produces an Oracle response for a request — the application seam a Shrine serves content through. Implement
/// this to serve anything; <see cref="StaticFileOracleHandler"/> and <see cref="DelegateOracleHandler"/> cover the
/// common cases.
/// </summary>
public interface IOracleHandler
{
    Task<OracleResponse> HandleAsync(OracleRequest request, CancellationToken cancellationToken = default);
}

/// <summary>An <see cref="IOracleHandler"/> backed by a delegate — the app-callback path.</summary>
public sealed class DelegateOracleHandler : IOracleHandler
{
    private readonly Func<OracleRequest, CancellationToken, Task<OracleResponse>> _handler;

    public DelegateOracleHandler(Func<OracleRequest, CancellationToken, Task<OracleResponse>> handler)
        => _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public DelegateOracleHandler(Func<OracleRequest, OracleResponse> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = (request, _) => Task.FromResult(handler(request));
    }

    public Task<OracleResponse> HandleAsync(OracleRequest request, CancellationToken cancellationToken = default)
        => _handler(request, cancellationToken);
}

/// <summary>
/// Runs an <see cref="IOracleHandler"/> over an <see cref="OracleSession"/>: reads requests and answers them until
/// the Pilgrim closes the session. A handler that throws yields a 500 for that request rather than tearing the
/// Shrine down.
/// </summary>
public static class OracleHost
{
    public static async Task ServeAsync(OracleSession session, IOracleHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(handler);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var evt = await session.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (evt is null)
                return; // the Pilgrim closed the session

            if (evt is not RequestReceived request)
                continue; // ignore stray non-request frames on the host side

            OracleResponse response;
            try
            {
                response = await handler.HandleAsync(request.Request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A single bad request must not take the Shrine down.
                response = new OracleResponse { Status = 500 };
            }

            await session.SendResponseAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }
}
