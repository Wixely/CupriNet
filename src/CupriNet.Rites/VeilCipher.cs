using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CupriNet.Alembic;

namespace CupriNet.Rites;

/// <summary>Which side of the Consecration a Veil speaks for. It decides which of the two directional keys seals.</summary>
public enum VeilRole
{
    /// <summary>The member who dialled (the Consecration initiator).</summary>
    Initiator,

    /// <summary>The member who answered (the Consecration responder).</summary>
    Responder,
}

/// <summary>
/// Encrypts and authenticates channel content under a Consecration session (Veil) key. Each message
/// gets a fresh random nonce and the sender's next sequence number, and the output is
/// <c>nonce || seal(sequence || plaintext)</c>. This is the inner confidentiality layer: content is protected
/// by the channel session key, independent of (and in addition to) any transport protection on the Vessel.
/// <para>
/// The session key is never used directly. Each rite, and each direction within it, seals under its own key
/// derived from it — <c>HKDF(sessionKey, info = "cuprinet/arcanum/veil/&lt;rite&gt;/&lt;role&gt;/v1")</c> — so a frame
/// cannot be replayed into another rite, nor handed back to the side that sent it. Whatever a rite leaves outside
/// the seal (a frame-type byte) goes in as associated data, so it cannot be changed without the frame failing.
/// </para>
/// <para>
/// Nonces stay random on purpose: a counter used as the nonce is only safe while nothing else ever seals under
/// the same key, and these constructors take a raw session key — two built on one key would repeat a nonce. A
/// 96-bit random nonce under a per-session, per-rite, per-direction key is safe far beyond any traffic a session
/// carries.
/// </para>
/// <para>
/// Replay is refused separately, and does not lean on the transport beneath: each frame carries a sequence number
/// under the seal, and a cipher opens a given number once. Frames may arrive a little out of order (within
/// <see cref="ReplayWindow"/>) — a rite's senders are not made to queue behind one another — but a frame seen
/// before, or older than the window, fails exactly as a forged one does. The state is per cipher: one sealing
/// cipher speaks to one opening cipher.
/// </para>
/// </summary>
public sealed class VeilCipher
{
    /// <summary>How far behind the newest frame opened an unseen frame may still arrive and be accepted.</summary>
    public const int ReplayWindow = 64;

    /// <summary>Bytes of the sequence number carried under the seal.</summary>
    public const int SequenceSize = 8;

    private readonly IAead _aead;
    private readonly byte[] _sealKey;
    private readonly byte[] _openKey;

    private long _sent;                 // the last sequence number sealed; the first frame is 1
    private readonly Lock _openGate = new();
    private ulong _newest;              // the highest sequence number opened
    private ulong _seen;                // bit i set: (_newest - i) has been opened

    /// <summary>Bytes a sealed frame is longer than its plaintext: the nonce, the sequence number and the tag.</summary>
    public int Overhead => _aead.NonceSize + SequenceSize + _aead.TagSize;

    /// <param name="sessionKey">The Consecration session key.</param>
    /// <param name="suite">The crypto suite.</param>
    /// <param name="rite">The rite this Veil serves (<c>epistle</c>, <c>conduit</c>, <c>oracle</c>, <c>auspice</c>).</param>
    /// <param name="role">This side's role in the Consecration.</param>
    public VeilCipher(ReadOnlyMemory<byte> sessionKey, ICryptoSuite suite, string rite, VeilRole role)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentException.ThrowIfNullOrEmpty(rite);
        _aead = suite.Aead;
        if (sessionKey.Length != _aead.KeySize)
            throw new ArgumentException($"Session key must be {_aead.KeySize} bytes for {suite.Name}.", nameof(sessionKey));

        var ikm = sessionKey.ToArray();
        var initiatorKey = suite.Kdf.DeriveKey(ikm, salt: [], info: Encoding.UTF8.GetBytes($"cuprinet/arcanum/veil/{rite}/initiator/v1"), _aead.KeySize);
        var responderKey = suite.Kdf.DeriveKey(ikm, salt: [], info: Encoding.UTF8.GetBytes($"cuprinet/arcanum/veil/{rite}/responder/v1"), _aead.KeySize);
        (_sealKey, _openKey) = role == VeilRole.Initiator ? (initiatorKey, responderKey) : (responderKey, initiatorKey);
    }

    public byte[] Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        var numbered = new byte[SequenceSize + plaintext.Length];
        BinaryPrimitives.WriteUInt64BigEndian(numbered, (ulong)Interlocked.Increment(ref _sent));
        plaintext.CopyTo(numbered.AsSpan(SequenceSize));

        var nonce = RandomNumberGenerator.GetBytes(_aead.NonceSize);
        var sealed_ = _aead.Seal(_sealKey, nonce, numbered, associatedData);
        var output = new byte[nonce.Length + sealed_.Length];
        nonce.CopyTo(output, 0);
        sealed_.CopyTo(output, nonce.Length);
        return output;
    }

    public byte[]? Open(ReadOnlySpan<byte> sealedMessage, ReadOnlySpan<byte> associatedData = default)
    {
        if (sealedMessage.Length < Overhead)
            return null;
        var nonce = sealedMessage[.._aead.NonceSize];
        var ciphertextAndTag = sealedMessage[_aead.NonceSize..];
        var numbered = _aead.Open(_openKey, nonce, ciphertextAndTag, associatedData);
        if (numbered is null || numbered.Length < SequenceSize)
            return null;

        // Only a frame that authenticated gets to move the window, so nothing forged can push real frames out of it.
        if (!TryAdmit(BinaryPrimitives.ReadUInt64BigEndian(numbered)))
            return null;
        return numbered[SequenceSize..];
    }

    /// <summary>Records <paramref name="sequence"/> as opened; false if it was opened before or is older than the window.</summary>
    private bool TryAdmit(ulong sequence)
    {
        if (sequence == 0)
            return false;
        lock (_openGate)
        {
            if (sequence > _newest)
            {
                var ahead = sequence - _newest;
                _seen = ahead >= ReplayWindow ? 1UL : (_seen << (int)ahead) | 1UL;
                _newest = sequence;
                return true;
            }

            var behind = _newest - sequence;
            if (behind >= ReplayWindow || (_seen & (1UL << (int)behind)) != 0)
                return false;
            _seen |= 1UL << (int)behind;
            return true;
        }
    }
}
