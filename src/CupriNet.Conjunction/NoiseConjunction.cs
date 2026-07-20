using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;
using CupriNet.Core;
using CupriNet.Noise;
using CupriNet.Vessel;

namespace CupriNet.Conjunction;

/// <summary>Thrown when a Noise-based transport handshake fails.</summary>
public sealed class NoiseConjunctionException(string message) : Exception(message);

/// <summary>The result of a Noise transport handshake: an encrypting vessel and the authenticated peer identity.</summary>
public sealed record NoiseConjunctionResult(NoiseVessel Vessel, Sigil PeerSigil, byte[] PeerSealPublicKey);

/// <summary>
/// Establishes a forward-secure, mutually-authenticated transport with a peer. A Noise_XX handshake
/// negotiates an encrypted session over a fresh per-connection Noise static key; then each side proves
/// its long-term identity by signing the Noise handshake hash with its Ed25519 Seal. Because the hash
/// binds both Noise static keys and the whole transcript, that signature ties the node's Sigil to this
/// specific session — a relay/MITM cannot transplant it. The returned <see cref="NoiseVessel"/> encrypts
/// all subsequent frames, so every layer above (Consecration, the rites) rides an encrypted transport.
/// </summary>
public static class NoiseConjunction
{
    private const ushort HandshakeStream = 0;
    private const byte BindingVersion = 1;

    /// <summary>Runs the initiator side (the node that dialled out).</summary>
    public static Task<NoiseConjunctionResult> InitiateAsync(
        IVessel vessel, NodeIdentity identity, Concordium network, ICryptoSuite suite,
        Sigil? expectedPeer = null, CancellationToken cancellationToken = default)
        => RunAsync(vessel, identity, network, suite, initiator: true, expectedPeer, cancellationToken);

    /// <summary>Runs the responder side (the node that accepted a connection).</summary>
    public static Task<NoiseConjunctionResult> AcceptAsync(
        IVessel vessel, NodeIdentity identity, Concordium network, ICryptoSuite suite,
        CancellationToken cancellationToken = default)
        => RunAsync(vessel, identity, network, suite, initiator: false, expectedPeer: null, cancellationToken);

    private static async Task<NoiseConjunctionResult> RunAsync(
        IVessel vessel, NodeIdentity identity, Concordium network, ICryptoSuite suite,
        bool initiator, Sigil? expectedPeer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(suite);

        var noiseStatic = suite.Agreement.Generate();
        var handshake = new NoiseHandshakeState(suite, initiator, (noiseStatic.PrivateKey, noiseStatic.PublicKey));

        // Noise_XX: -> e ; <- e, ee, s, es ; -> s, se
        if (initiator)
        {
            await SendAsync(vessel, handshake.WriteMessage(), cancellationToken).ConfigureAwait(false);
            handshake.ReadMessage(await ReceiveAsync(vessel, cancellationToken).ConfigureAwait(false));
            await SendAsync(vessel, handshake.WriteMessage(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            handshake.ReadMessage(await ReceiveAsync(vessel, cancellationToken).ConfigureAwait(false));
            await SendAsync(vessel, handshake.WriteMessage(), cancellationToken).ConfigureAwait(false);
            handshake.ReadMessage(await ReceiveAsync(vessel, cancellationToken).ConfigureAwait(false));
        }

        var transport = handshake.Split();
        var secure = new NoiseVessel(vessel, transport);

        // Identity binding over the now-encrypted transport: sign the handshake hash with the Seal.
        var myBinding = BuildBinding(identity, network, suite, transport.HandshakeHash);
        byte[] peerBinding;
        if (initiator)
        {
            await secure.SendAsync(HandshakeStream, myBinding, cancellationToken).ConfigureAwait(false);
            peerBinding = await ReceiveAsync(secure, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            peerBinding = await ReceiveAsync(secure, cancellationToken).ConfigureAwait(false);
            await secure.SendAsync(HandshakeStream, myBinding, cancellationToken).ConfigureAwait(false);
        }

        var (peerNetwork, peerSealPublicKey, peerSignature) = ParseBinding(peerBinding);
        if (peerNetwork != network)
            throw new NoiseConjunctionException("Peer belongs to a different network (Concordium).");
        if (!suite.Verifier.Verify(transport.HandshakeHash, peerSignature, peerSealPublicKey))
            throw new NoiseConjunctionException("Peer identity binding did not verify against the handshake.");

        var peerSigil = Sigil.FromSealPublicKey(peerSealPublicKey);
        if (expectedPeer is { } expected && peerSigil != expected)
            throw new NoiseConjunctionException("Peer Sigil did not match the expected identity from the Intonation.");

        return new NoiseConjunctionResult(secure, peerSigil, peerSealPublicKey);
    }

    private static byte[] BuildBinding(NodeIdentity identity, Concordium network, ICryptoSuite suite, byte[] handshakeHash)
    {
        var signature = suite.CreateSigner(identity.Seal.PrivateKey).Sign(handshakeHash);
        var w = new CodexWriter();
        w.WriteByte(BindingVersion);
        w.WriteString(network.Value);
        w.WriteBytes(identity.Seal.PublicKey);
        w.WriteBytes(signature);
        return w.ToArray();
    }

    private static (Concordium Network, byte[] SealPublicKey, byte[] Signature) ParseBinding(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        if (r.ReadByte() != BindingVersion)
            throw new NoiseConjunctionException("Unsupported identity-binding version.");
        var network = new Concordium(r.ReadString());
        var sealPublicKey = r.ReadBytes().ToArray();
        var signature = r.ReadBytes().ToArray();
        return (network, sealPublicKey, signature);
    }

    private static ValueTask SendAsync(IVessel vessel, byte[] payload, CancellationToken cancellationToken)
        => vessel.SendAsync(HandshakeStream, payload, cancellationToken);

    private static async Task<byte[]> ReceiveAsync(IVessel vessel, CancellationToken cancellationToken)
    {
        var frame = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new NoiseConjunctionException("Vessel closed during the handshake.");
        if (frame.StreamId != HandshakeStream)
            throw new NoiseConjunctionException($"Unexpected frame on stream {frame.StreamId} during the handshake.");
        return frame.Payload;
    }
}
