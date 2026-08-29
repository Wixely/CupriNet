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

    /// <summary>
    /// Marks a responder message that is a refusal rather than a binding. Distinct from every valid
    /// <see cref="BindingVersion"/>, so an initiator reads a typed reason instead of a parse failure.
    /// </summary>
    private const byte RefusalMarker = 0xFF;

    private const string ShrineTargetInfo = "cuprinet:shrine-target:v1";

    /// <summary>
    /// The blinded name of the Shrine an initiator is asking for: HKDF over the target's <see cref="Sigil"/>, salted
    /// with this session's Noise handshake hash.
    ///
    /// <para>Blinded rather than sent plainly for two reasons. A host that does <em>not</em> serve the requested site
    /// learns only "not one of mine" — it never learns which site a visitor wanted, which matters because the visitor
    /// must name its target before the host has proved it holds anything. And because the handshake hash salts it,
    /// the value differs on every visit, so it is no use as a handle for linking one visit to the next.</para>
    ///
    /// <para>The host recomputes this for each Signet it hosts and compares — N cheap derivations, no lookup table,
    /// and nothing to store. It is not a secret: anyone holding the Sigil (i.e. the site's public address) can
    /// compute it for a session whose handshake hash they know. It hides the target from parties who do not already
    /// know it, which is exactly the set that should not learn it.</para>
    /// </summary>
    public static byte[] ShrineTargetTag(ICryptoSuite suite, ReadOnlySpan<byte> handshakeHash, Sigil target)
    {
        ArgumentNullException.ThrowIfNull(suite);
        return suite.Kdf.DeriveKey(target.Span, handshakeHash, System.Text.Encoding.UTF8.GetBytes(ShrineTargetInfo), 32);
    }

    /// <summary>
    /// Runs the initiator side (the node that dialled out). When <paramref name="expectedPeer"/> is the Signet of a
    /// Shrine, pass it as <paramref name="target"/> too: an endpoint serving several Shrines needs to be told which
    /// one is wanted, and this is what tells it — blinded, and inside the encrypted transport.
    /// </summary>
    public static Task<NoiseConjunctionResult> InitiateAsync(
        IVessel vessel, NodeIdentity identity, Concordium network, ICryptoSuite suite,
        Sigil? expectedPeer = null, CancellationToken cancellationToken = default, Sigil? target = null)
        => RunAsync(vessel, identity, network, suite, initiator: true, expectedPeer, null, target, cancellationToken);

    /// <summary>Runs the responder side (the node that accepted a connection).</summary>
    public static Task<NoiseConjunctionResult> AcceptAsync(
        IVessel vessel, NodeIdentity identity, Concordium network, ICryptoSuite suite,
        CancellationToken cancellationToken = default)
        => RunAsync(vessel, identity, network, suite, initiator: false, expectedPeer: null, null, null, cancellationToken);

    /// <summary>
    /// Responder side for an endpoint that may serve several identities. <paramref name="resolveIdentity"/> is handed
    /// the blinded target the initiator asked for (see <see cref="ShrineTargetTag"/>) and returns the identity to
    /// present, or null to refuse the visit with a reason the initiator can read.
    ///
    /// <para>This is why the responder reads the peer's binding before sending its own: the choice of which key to
    /// present cannot be made until the visitor has said what it came for, and it says so inside Noise. No selector
    /// ever crosses the wire in the clear, so the network learns neither that this is a site visit nor which site.</para>
    /// </summary>
    public static Task<NoiseConjunctionResult> AcceptAsync(
        IVessel vessel, NodeIdentity fallbackIdentity, Concordium network, ICryptoSuite suite,
        Func<ReadOnlyMemory<byte>, byte[], NodeIdentity?> resolveIdentity, CancellationToken cancellationToken = default)
        => RunAsync(vessel, fallbackIdentity, network, suite, initiator: false, expectedPeer: null,
            resolveIdentity ?? throw new ArgumentNullException(nameof(resolveIdentity)), null, cancellationToken);

    private static async Task<NoiseConjunctionResult> RunAsync(
        IVessel vessel, NodeIdentity identity, Concordium network, ICryptoSuite suite,
        bool initiator, Sigil? expectedPeer, Func<ReadOnlyMemory<byte>, byte[], NodeIdentity?>? resolveIdentity,
        Sigil? target, CancellationToken cancellationToken)
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
        byte[] peerBinding;
        if (initiator)
        {
            // The initiator names the Shrine it wants, blinded (see ShrineTargetTag). Empty for ordinary pairing.
            var targetTag = target is { } wanted
                ? ShrineTargetTag(suite, transport.HandshakeHash, wanted)
                : [];
            await secure.SendAsync(
                HandshakeStream,
                BuildBinding(identity, network, suite, transport.HandshakeHash, mine.Min, mine.Max, targetTag),
                cancellationToken).ConfigureAwait(false);
            peerBinding = await ReceiveAsync(secure, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Read first, then answer. A responder serving several Shrines cannot know which identity to present
            // until the initiator has said, and it says so in here — inside Noise, so nothing on the path learns it.
            peerBinding = await ReceiveAsync(secure, cancellationToken).ConfigureAwait(false);

            if (resolveIdentity is not null)
            {
                var requested = ParseBinding(peerBinding).TargetTag;
                if (resolveIdentity(requested, transport.HandshakeHash) is not { } resolved)
                {
                    // Say so rather than falling silent or presenting the wrong key: an unanswerable visit should
                    // fail here, with a reason, not later as a session in which every rite is mysteriously dead.
                    await secure.SendAsync(HandshakeStream, BuildRefusal("no such Shrine at this endpoint"), cancellationToken)
                        .ConfigureAwait(false);
                    throw new NoiseConjunctionException("No hosted Shrine matched the address the visitor pinned.");
                }

                identity = resolved;
            }

            await secure.SendAsync(
                HandshakeStream,
                BuildBinding(identity, network, suite, transport.HandshakeHash, mine.Min, mine.Max),
                cancellationToken).ConfigureAwait(false);
        }

        var (peerNetwork, peerSealPublicKey, peerMin, peerMax, peerSignature, _) = ParseBinding(peerBinding);
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
        NodeIdentity identity, Concordium network, ICryptoSuite suite, byte[] handshakeHash, ushort min, ushort max,
        ReadOnlySpan<byte> targetTag = default)
    {
        var signature = suite.CreateSigner(identity.Seal.PrivateKey).Sign(BindingContext(handshakeHash, min, max));
        var w = new CodexWriter();
        w.WriteByte(BindingVersion);
        w.WriteString(network.Value);
        w.WriteBytes(identity.Seal.PublicKey);
        w.WriteVarUInt(min);
        w.WriteVarUInt(max);
        w.WriteBytes(signature);
        // Trailing field, same backward-compatible pattern as Moniker and WebRtcEndpoint: a peer that predates it
        // simply sends nothing here, and a host that reads nothing falls back to the one Shrine it serves. It sits
        // outside the signed context deliberately — it names what the initiator wants, not what it is, and the
        // handshake hash inside the tag already binds it to this session.
        w.WriteBytes(targetTag);
        return w.ToArray();
    }

    private static (Concordium Network, byte[] SealPublicKey, ushort Min, ushort Max, byte[] Signature, byte[] TargetTag)
        ParseBinding(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        var version = r.ReadByte();
        if (version == RefusalMarker)
            throw new NoiseConjunctionException(
                $"The host refused the visit: {r.ReadString()}. It answered on this endpoint but serves no Shrine at " +
                "the address that was pinned.");
        if (version != BindingVersion)
            throw new NoiseConjunctionException("Unsupported identity-binding version.");
        var network = new Concordium(r.ReadString());
        var sealPublicKey = r.ReadBytes().ToArray();
        var min = ReadOrdinal(ref r);
        var max = ReadOrdinal(ref r);
        var signature = r.ReadBytes().ToArray();
        var targetTag = r.Remaining > 0 ? r.ReadBytes().ToArray() : [];
        return (network, sealPublicKey, min, max, signature, targetTag);
    }

    private static byte[] BuildRefusal(string reason)
    {
        var w = new CodexWriter();
        w.WriteByte(RefusalMarker);
        w.WriteString(reason);
        return w.ToArray();
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
