using System.Security.Cryptography;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.Arcanum;

/// <summary>Thrown when a Consecration fails (wrong Watchword, stale epoch, or a broken transcript).</summary>
public sealed class ConsecrationException(string message) : Exception(message);

/// <summary>The result of a completed Consecration: the agreed epoch and the fresh Veil session key.</summary>
public sealed record Consecration(long Epoch, byte[] SessionKey);

/// <summary>
/// The second-layer channel handshake. The transport peer and the channel member are separate trust
/// domains, so channel confidentiality is established here, inside the Vessel — never from the transport
/// session alone. Both sides prove possession of the Watchword-derived ConcordKey via a key-confirmation
/// bound to a transcript over: version, the channel's Ascendant, the epoch, both transport peer Sigils,
/// and both ephemeral nonces. A fresh Veil session key is derived from the same transcript. This binding
/// prevents a channel session being transplanted onto another connection, channel, or epoch.
/// </summary>
public static class ConsecrationHandshake
{
    /// <summary>Logical stream for channel-handshake control traffic (0 = Conjunction, 1 = peer exchange).</summary>
    public const ushort ChannelStream = 2;

    private const byte Version = 1;
    private const int NonceSize = 16;
    private const int TagSize = 32;
    private const int SessionKeySize = 32;

    private static readonly byte[] VeilSessionInfo = "cuprinet/arcanum/veil-session/v1"u8.ToArray();
    private static readonly byte[] ConfirmInitiator = "cuprinet/arcanum/consecration/initiator/v1"u8.ToArray();
    private static readonly byte[] ConfirmResponder = "cuprinet/arcanum/consecration/responder/v1"u8.ToArray();

    private enum MessageType : byte
    {
        Hello = 1,
        Confirm = 2,
    }

    /// <summary>Runs the initiator side (the member who dialled the provider).</summary>
    public static Task<Consecration> InitiateAsync(
        VesselSession vessel, ArcanumKeys keys, Sigil localSigil, Sigil remoteSigil, DateTimeOffset now,
        ICryptoSuite suite, ushort stream = ChannelStream, long turningSeconds = Glyph.DefaultTurningSeconds, CancellationToken cancellationToken = default)
        => RunAsync(vessel, keys, initiatorSigil: localSigil, responderSigil: remoteSigil, isInitiator: true, now, suite, stream, turningSeconds, cancellationToken);

    /// <summary>Runs the responder side (the provider hosting the Arcanum).</summary>
    public static Task<Consecration> AcceptAsync(
        VesselSession vessel, ArcanumKeys keys, Sigil localSigil, Sigil remoteSigil, DateTimeOffset now,
        ICryptoSuite suite, ushort stream = ChannelStream, long turningSeconds = Glyph.DefaultTurningSeconds, CancellationToken cancellationToken = default)
        => RunAsync(vessel, keys, initiatorSigil: remoteSigil, responderSigil: localSigil, isInitiator: false, now, suite, stream, turningSeconds, cancellationToken);

    private static async Task<Consecration> RunAsync(
        VesselSession vessel, ArcanumKeys keys, Sigil initiatorSigil, Sigil responderSigil, bool isInitiator,
        DateTimeOffset now, ICryptoSuite suite, ushort stream, long turningSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(suite);

        var myEpoch = Glyph.Epoch(now, turningSeconds);
        var myNonce = RandomNumberGenerator.GetBytes(NonceSize);

        long epoch;
        byte[] initiatorNonce, responderNonce;

        if (isInitiator)
        {
            await SendHelloAsync(vessel, stream, myEpoch, myNonce, cancellationToken).ConfigureAwait(false);
            var peer = await ReceiveHelloAsync(vessel, stream, cancellationToken).ConfigureAwait(false);
            epoch = myEpoch; // the initiator's epoch is authoritative
            initiatorNonce = myNonce;
            responderNonce = peer.Nonce;
        }
        else
        {
            var peer = await ReceiveHelloAsync(vessel, stream, cancellationToken).ConfigureAwait(false);
            if (Math.Abs(peer.Epoch - myEpoch) > 1)
                throw new ConsecrationException("Consecration epoch is outside the acceptable window.");
            await SendHelloAsync(vessel, stream, myEpoch, myNonce, cancellationToken).ConfigureAwait(false);
            epoch = peer.Epoch; // adopt the initiator's epoch
            initiatorNonce = peer.Nonce;
            responderNonce = myNonce;
        }

        var transcript = BuildTranscript(suite, keys, epoch, initiatorSigil, responderSigil, initiatorNonce, responderNonce);
        var myTag = Tag(suite, keys.ConcordKey, transcript, isInitiator ? ConfirmInitiator : ConfirmResponder);
        var expectedPeerTag = Tag(suite, keys.ConcordKey, transcript, isInitiator ? ConfirmResponder : ConfirmInitiator);

        byte[] peerTag;
        if (isInitiator)
        {
            await SendConfirmAsync(vessel, stream, myTag, cancellationToken).ConfigureAwait(false);
            peerTag = await ReceiveConfirmAsync(vessel, stream, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            peerTag = await ReceiveConfirmAsync(vessel, stream, cancellationToken).ConfigureAwait(false);
            await SendConfirmAsync(vessel, stream, myTag, cancellationToken).ConfigureAwait(false);
        }

        if (!CryptographicOperations.FixedTimeEquals(peerTag, expectedPeerTag))
            throw new ConsecrationException("Peer failed channel key confirmation (wrong Watchword or epoch).");

        var sessionKey = suite.Kdf.DeriveKey(keys.ConcordKey, transcript, VeilSessionInfo, SessionKeySize);
        return new Consecration(epoch, sessionKey);
    }

    private static byte[] BuildTranscript(ICryptoSuite suite, ArcanumKeys keys, long epoch, Sigil initiator, Sigil responder, byte[] initiatorNonce, byte[] responderNonce)
    {
        var w = new CodexWriter();
        w.WriteByte(Version);
        w.WriteBytes(keys.Ascendant.Span); // binds the session to this channel
        w.WriteUInt64((ulong)epoch);
        w.WriteBytes(initiator.Span);
        w.WriteBytes(responder.Span);
        w.WriteBytes(initiatorNonce);
        w.WriteBytes(responderNonce);
        return suite.Hash.Sha256(w.ToArray());
    }

    private static byte[] Tag(ICryptoSuite suite, byte[] concordKey, byte[] transcript, byte[] roleInfo)
        => suite.Kdf.DeriveKey(concordKey, salt: transcript, info: roleInfo, TagSize);

    private static async Task SendHelloAsync(VesselSession vessel, ushort stream, long epoch, byte[] nonce, CancellationToken cancellationToken)
    {
        var w = new CodexWriter();
        w.WriteByte((byte)MessageType.Hello);
        w.WriteByte(Version);
        w.WriteUInt64((ulong)epoch);
        w.WriteBytes(nonce);
        await vessel.SendAsync(stream, w.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(long Epoch, byte[] Nonce)> ReceiveHelloAsync(VesselSession vessel, ushort stream, CancellationToken cancellationToken)
    {
        var payload = await ReceiveOnAsync(vessel, stream, cancellationToken).ConfigureAwait(false);
        var r = new CodexReader(payload);
        if ((MessageType)r.ReadByte() != MessageType.Hello)
            throw new ConsecrationException("Expected a channel Hello.");
        if (r.ReadByte() != Version)
            throw new ConsecrationException("Unsupported channel handshake version.");
        var epoch = (long)r.ReadUInt64();
        var nonce = r.ReadBytes().ToArray();
        return (epoch, nonce);
    }

    private static async Task SendConfirmAsync(VesselSession vessel, ushort stream, byte[] tag, CancellationToken cancellationToken)
    {
        var w = new CodexWriter();
        w.WriteByte((byte)MessageType.Confirm);
        w.WriteBytes(tag);
        await vessel.SendAsync(stream, w.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReceiveConfirmAsync(VesselSession vessel, ushort stream, CancellationToken cancellationToken)
    {
        var payload = await ReceiveOnAsync(vessel, stream, cancellationToken).ConfigureAwait(false);
        var r = new CodexReader(payload);
        if ((MessageType)r.ReadByte() != MessageType.Confirm)
            throw new ConsecrationException("Expected a channel Confirm.");
        return r.ReadBytes().ToArray();
    }

    private static async Task<byte[]> ReceiveOnAsync(VesselSession vessel, ushort stream, CancellationToken cancellationToken)
    {
        var frame = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new ConsecrationException("Vessel closed during the Consecration.");
        if (frame.StreamId != stream)
            throw new ConsecrationException($"Unexpected frame on stream {frame.StreamId} during the Consecration.");
        return frame.Payload;
    }
}
