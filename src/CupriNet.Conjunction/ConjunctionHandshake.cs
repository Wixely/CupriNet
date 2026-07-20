using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Core;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.Conjunction;

/// <summary>Thrown when a Conjunction handshake fails (bad network, bad binding, or wrong peer).</summary>
public sealed class ConjunctionException(string message) : Exception(message);

/// <summary>The result of a completed handshake: the authenticated peer identity.</summary>
public sealed record Conjunction(Sigil PeerSigil, byte[] PeerSealPublicKey);

/// <summary>
/// A minimal mutually-authenticated pairing handshake over a <see cref="Vessel"/>. Both sides exchange
/// a Hello (identity + nonce), derive a shared transcript, and sign it; each verifies the other's
/// signature through the Alembic seam. This has the shape of a transcript-bound Noise handshake; the
/// real Noise_XX/IK state machine replaces the internals at the Tempering (Phase 1b) without changing
/// the message flow. Under the Simulacrum the signatures are trivially accepted.
/// </summary>
public static class ConjunctionHandshake
{
    /// <summary>The logical stream reserved for handshake control traffic.</summary>
    public const ushort ControlStream = 0;

    private const byte Version = 1;
    private const int NonceSize = 16;

    /// <summary>Runs the initiator side (the node that dialled out using an Intonation).</summary>
    /// <param name="expectedPeer">If set, the handshake fails unless the peer proves this Sigil.</param>
    public static Task<Conjunction> InitiateAsync(
        VesselSession vessel, NodeIdentity identity, Concordium network, ICryptoSuite suite,
        Sigil? expectedPeer = null, CancellationToken cancellationToken = default)
        => RunAsync(vessel, identity, network, suite, isInitiator: true, expectedPeer, cancellationToken);

    /// <summary>Runs the responder side (the node that accepted an inbound Vessel).</summary>
    public static Task<Conjunction> AcceptAsync(
        VesselSession vessel, NodeIdentity identity, Concordium network, ICryptoSuite suite,
        CancellationToken cancellationToken = default)
        => RunAsync(vessel, identity, network, suite, isInitiator: false, expectedPeer: null, cancellationToken);

    private static async Task<Conjunction> RunAsync(
        VesselSession vessel, NodeIdentity identity, Concordium network, ICryptoSuite suite,
        bool isInitiator, Sigil? expectedPeer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(suite);

        var myHello = new Hello(Version, network, identity.PublicKey.ToArray(), RandomNonce());
        var myHelloBytes = myHello.Encode();

        byte[] peerHelloBytes;
        if (isInitiator)
        {
            await vessel.SendAsync(ControlStream, myHelloBytes, cancellationToken).ConfigureAwait(false);
            peerHelloBytes = await ReceiveAsync(vessel, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            peerHelloBytes = await ReceiveAsync(vessel, cancellationToken).ConfigureAwait(false);
            await vessel.SendAsync(ControlStream, myHelloBytes, cancellationToken).ConfigureAwait(false);
        }

        var peerHello = Hello.Decode(peerHelloBytes);
        if (peerHello.Version != Version)
            throw new ConjunctionException($"Unsupported peer handshake version {peerHello.Version}.");
        if (peerHello.Network != network)
            throw new ConjunctionException("Peer belongs to a different network (Concordium).");

        // The transcript always concatenates initiator-hello first, so both sides agree byte-for-byte.
        var (initiatorHello, responderHello) = isInitiator
            ? (myHelloBytes, peerHelloBytes)
            : (peerHelloBytes, myHelloBytes);
        var transcript = Transcript(suite, initiatorHello, responderHello);

        var myBinding = new Binding(suite.CreateSigner(identity.Seal.PrivateKey).Sign(transcript));
        var myBindingBytes = myBinding.Encode();

        byte[] peerBindingBytes;
        if (isInitiator)
        {
            await vessel.SendAsync(ControlStream, myBindingBytes, cancellationToken).ConfigureAwait(false);
            peerBindingBytes = await ReceiveAsync(vessel, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            peerBindingBytes = await ReceiveAsync(vessel, cancellationToken).ConfigureAwait(false);
            await vessel.SendAsync(ControlStream, myBindingBytes, cancellationToken).ConfigureAwait(false);
        }

        var peerBinding = Binding.Decode(peerBindingBytes);
        if (!suite.Verifier.Verify(transcript, peerBinding.Signature, peerHello.SealPublicKey))
            throw new ConjunctionException("Peer binding signature did not verify.");

        var peerSigil = Sigil.FromSealPublicKey(peerHello.SealPublicKey);
        if (expectedPeer is { } expected && peerSigil != expected)
            throw new ConjunctionException("Peer Sigil did not match the expected identity from the Intonation.");

        return new Conjunction(peerSigil, peerHello.SealPublicKey);
    }

    private static byte[] Transcript(ICryptoSuite suite, ReadOnlySpan<byte> initiatorHello, ReadOnlySpan<byte> responderHello)
    {
        var combined = new byte[initiatorHello.Length + responderHello.Length];
        initiatorHello.CopyTo(combined);
        responderHello.CopyTo(combined.AsSpan(initiatorHello.Length));
        return suite.Hash.Sha256(combined);
    }

    private static async Task<byte[]> ReceiveAsync(VesselSession vessel, CancellationToken cancellationToken)
    {
        var frame = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new ConjunctionException("Vessel closed during the handshake.");
        if (frame.StreamId != ControlStream)
            throw new ConjunctionException($"Unexpected frame on stream {frame.StreamId} during the handshake.");
        return frame.Payload;
    }

    private static byte[] RandomNonce() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(NonceSize);
}
