using CupriMark;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;
using CupriNet.Core;
using CupriNet.Marks;
using CupriNet.Noise;
using CupriNet.Vessel;

namespace CupriNet.Conjunction;

/// <summary>Thrown when a Noise-based transport handshake fails.</summary>
public sealed class NoiseConjunctionException(string message) : Exception(message);

/// <summary>
/// The result of a Noise transport handshake: an encrypting vessel, the authenticated peer identity, and
/// the CupriMark-negotiated Conjunction protocol version (the highest both peers speak).
/// </summary>
public sealed record NoiseConjunctionResult(NoiseVessel Vessel, Sigil PeerSigil, byte[] PeerSealPublicKey, ushort ConjunctionVersion);

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
        if (!suite.IsSecure)
            throw new NoiseConjunctionException("Noise transport requires a secure crypto suite (real Diffie–Hellman).");

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

        // Identity binding over the now-encrypted transport. Each side also advertises the ordinal range of
        // the Conjunction protocol it speaks (CupriMark), and signs the handshake hash TOGETHER WITH that
        // range: so the advertised range is bound to both this identity and this session — a downgrade that
        // rewrites the range can't survive signature verification. The range travels inside the Noise-
        // encrypted channel, so it is confidential as well (no on-path observer learns how old a build is).
        var mine = CupriMarks.Supported(CupriMarks.Conjunction);
        var myBinding = BuildBinding(identity, network, suite, transport.HandshakeHash, mine.Min, mine.Max);
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

        var (peerNetwork, peerSealPublicKey, peerMin, peerMax, peerSignature) = ParseBinding(peerBinding);
        if (peerNetwork != network)
            throw new NoiseConjunctionException("Peer belongs to a different network (Concordium).");
        if (peerMin > peerMax)
            throw new NoiseConjunctionException("Peer advertised a malformed Conjunction version range.");
        if (!suite.Verifier.Verify(BindingContext(transport.HandshakeHash, peerMin, peerMax), peerSignature, peerSealPublicKey))
            throw new NoiseConjunctionException("Peer identity binding did not verify against the handshake.");

        var peerSigil = Sigil.FromSealPublicKey(peerSealPublicKey);
        if (expectedPeer is { } expected && peerSigil != expected)
            throw new NoiseConjunctionException("Peer Sigil did not match the expected identity from the Intonation.");

        // Range-negotiate the Conjunction version instead of hard-failing on an equality check: the highest
        // ordinal both speak, at or above our security floor. A peer too old to meet the floor is rejected
        // with a typed reason rather than silently partitioned.
        var negotiation = CupriMarks.Negotiate(CupriMarks.Conjunction, OrdinalRange.Create(peerMin, peerMax));
        if (!negotiation.Accepted)
            throw new NoiseConjunctionException(
                $"Conjunction version negotiation failed ({negotiation.Reason}); peer offered [{peerMin}..{peerMax}], our floor is {negotiation.EffectiveFloor}.");

        return new NoiseConjunctionResult(secure, peerSigil, peerSealPublicKey, negotiation.SelectedOrdinal);
    }

    private static byte[] BuildBinding(
        NodeIdentity identity, Concordium network, ICryptoSuite suite, byte[] handshakeHash, ushort min, ushort max)
    {
        var signature = suite.CreateSigner(identity.Seal.PrivateKey).Sign(BindingContext(handshakeHash, min, max));
        var w = new CodexWriter();
        w.WriteByte(BindingVersion);
        w.WriteString(network.Value);
        w.WriteBytes(identity.Seal.PublicKey);
        w.WriteVarUInt(min);
        w.WriteVarUInt(max);
        w.WriteBytes(signature);
        return w.ToArray();
    }

    private static (Concordium Network, byte[] SealPublicKey, ushort Min, ushort Max, byte[] Signature) ParseBinding(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        if (r.ReadByte() != BindingVersion)
            throw new NoiseConjunctionException("Unsupported identity-binding version.");
        var network = new Concordium(r.ReadString());
        var sealPublicKey = r.ReadBytes().ToArray();
        var min = ReadOrdinal(ref r);
        var max = ReadOrdinal(ref r);
        var signature = r.ReadBytes().ToArray();
        return (network, sealPublicKey, min, max, signature);
    }

    private static ushort ReadOrdinal(ref CodexReader r)
    {
        var value = r.ReadVarUInt();
        if (value > ushort.MaxValue)
            throw new NoiseConjunctionException("Peer advertised an out-of-range Conjunction ordinal.");
        return (ushort)value;
    }

    /// <summary>
    /// The exact bytes signed by the identity binding: the Noise handshake hash followed by the advertised
    /// Conjunction ordinal range (4 bytes, big-endian). Both peers derive this identically, so signing it
    /// ties the advertised range to the session and makes any tampering fail verification.
    /// </summary>
    private static byte[] BindingContext(byte[] handshakeHash, ushort min, ushort max)
    {
        var context = new byte[handshakeHash.Length + 4];
        handshakeHash.CopyTo(context, 0);
        var i = handshakeHash.Length;
        context[i] = (byte)(min >> 8);
        context[i + 1] = (byte)min;
        context[i + 2] = (byte)(max >> 8);
        context[i + 3] = (byte)max;
        return context;
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
