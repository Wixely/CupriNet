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
/// gets a fresh random nonce, and the output is <c>nonce || ciphertext+tag</c>. This is the inner
/// confidentiality layer: content is protected by the channel session key, independent of (and in
/// addition to) any transport protection on the Vessel.
/// <para>
/// The session key is never used directly. Each rite, and each direction within it, seals under its own key
/// derived from it — <c>HKDF(sessionKey, info = "cuprinet/arcanum/veil/&lt;rite&gt;/&lt;role&gt;/v1")</c> — so a frame
/// cannot be replayed into another rite, nor handed back to the side that sent it. Whatever a rite leaves outside
/// the seal (a frame-type byte) goes in as associated data, so it cannot be changed without the frame failing.
/// </para>
/// <para>
/// Nonces stay random on purpose: several rite sessions seal under keys from the one session key, concurrently
/// and with no shared state, which a counter would need. A 96-bit random nonce under a per-session, per-rite,
/// per-direction key is safe far beyond any traffic a session carries. The wire shape and overhead — a nonce and
/// a tag — are unchanged, so every size limit computed around them still holds.
/// </para>
/// </summary>
public sealed class VeilCipher
{
    private readonly IAead _aead;
    private readonly byte[] _sealKey;
    private readonly byte[] _openKey;

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
        var nonce = RandomNumberGenerator.GetBytes(_aead.NonceSize);
        var sealed_ = _aead.Seal(_sealKey, nonce, plaintext, associatedData);
        var output = new byte[nonce.Length + sealed_.Length];
        nonce.CopyTo(output, 0);
        sealed_.CopyTo(output, nonce.Length);
        return output;
    }

    public byte[]? Open(ReadOnlySpan<byte> sealedMessage, ReadOnlySpan<byte> associatedData = default)
    {
        if (sealedMessage.Length < _aead.NonceSize + _aead.TagSize)
            return null;
        var nonce = sealedMessage[.._aead.NonceSize];
        var ciphertextAndTag = sealedMessage[_aead.NonceSize..];
        return _aead.Open(_openKey, nonce, ciphertextAndTag, associatedData);
    }
}
