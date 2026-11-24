using System.Net;
using CupriNet.Codex;
using CupriNet.Noise;
using CupriNet.Vessel;

namespace CupriNet.Conjunction;

/// <summary>
/// An <see cref="IVessel"/> that transparently encrypts every frame with a completed Noise transport. The
/// stream id is sealed together with the payload: every frame travels on one carrier stream of the underlying
/// vessel, and the real id is the first thing inside the ciphertext. So the id is authenticated — it cannot be
/// rewritten on the way without the frame failing to open — and an observer cannot tell which rite a frame
/// belongs to, or what kind of session it is watching. Sends are serialised so the Noise nonce order matches the
/// wire order; receives are single-consumer (the mux pump), so decryption stays in step.
/// </summary>
public sealed class NoiseVessel : IVessel
{
    /// <summary>The one stream id that ever appears outside the ciphertext.</summary>
    private const ushort CarrierStream = 0;

    private readonly IVessel _inner;
    private readonly NoiseTransport _transport;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _disposed;

    public NoiseVessel(IVessel inner, NoiseTransport transport)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;

    public EndPoint? LocalEndPoint => _inner.LocalEndPoint;

    /// <summary>
    /// The Noise handshake hash of this session: a value both ends hold and no other session shares. Signing it
    /// binds a statement to this connection, so it cannot be replayed on another.
    /// </summary>
    public ReadOnlyMemory<byte> HandshakeHash => _transport.HandshakeHash;

    public async ValueTask SendAsync(ushort streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var w = new CodexWriter();
            w.WriteVarUInt(streamId);
            var plaintext = new byte[w.Length + payload.Length];
            w.WrittenSpan.CopyTo(plaintext);
            payload.Span.CopyTo(plaintext.AsSpan(w.Length));

            var ciphertext = _transport.Encrypt(plaintext);
            await _inner.SendAsync(CarrierStream, ciphertext, cancellationToken).ConfigureAwait(false);
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

        // Nothing but the carrier is ever sent, so anything else was put there by someone else.
        if (frame.Value.StreamId != CarrierStream)
            throw new NoiseException($"Unexpected frame on outer stream {frame.Value.StreamId}; a sealed transport uses only stream {CarrierStream}.");

        var plaintext = _transport.Decrypt(frame.Value.Payload); // throws NoiseException on tamper
        ulong streamId;
        int header;
        try
        {
            var r = new CodexReader(plaintext);
            streamId = r.ReadVarUInt();
            header = r.Position;
        }
        catch (CodexFormatException)
        {
            throw new NoiseException("Sealed frame carries no stream id.");
        }
        if (streamId > ushort.MaxValue)
            throw new NoiseException("Sealed frame carries an out-of-range stream id.");

        return new VesselFrame((ushort)streamId, plaintext[header..]);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;                                     // idempotent: see VesselMux.DisposeAsync

        _sendLock.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
