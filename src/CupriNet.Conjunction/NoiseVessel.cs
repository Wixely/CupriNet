using System.Net;
using CupriNet.Noise;
using CupriNet.Vessel;

namespace CupriNet.Conjunction;

/// <summary>
/// An <see cref="IVessel"/> that transparently encrypts every frame with a completed Noise transport.
/// Frame stream ids are preserved; only payloads are sealed. Sends are serialised so the Noise nonce
/// order matches the wire order; receives are single-consumer (the mux pump), so decryption stays in step.
/// </summary>
public sealed class NoiseVessel : IVessel
{
    private readonly IVessel _inner;
    private readonly NoiseTransport _transport;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public NoiseVessel(IVessel inner, NoiseTransport transport)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;

    public EndPoint? LocalEndPoint => _inner.LocalEndPoint;

    public async ValueTask SendAsync(ushort streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ciphertext = _transport.Encrypt(payload.Span);
            await _inner.SendAsync(streamId, ciphertext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<VesselFrame?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var frame = await _inner.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null)
            return null;

        var plaintext = _transport.Decrypt(frame.Value.Payload); // throws NoiseException on tamper
        return new VesselFrame(frame.Value.StreamId, plaintext);
    }

    public async ValueTask DisposeAsync()
    {
        _sendLock.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
