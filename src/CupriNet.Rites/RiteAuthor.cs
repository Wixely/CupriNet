using System.Security.Cryptography;
using System.Text;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;

namespace CupriNet.Rites;

/// <summary>Canonical wire form for the optional author envelope trailer shared by the rite codecs.</summary>
internal static class AuthorEnvelope
{
    public static void Write(CodexWriter w, byte[]? publicKey, byte[]? signature)
    {
        if (publicKey is null || signature is null)
        {
            w.WriteByte(0);
            return;
        }
        w.WriteByte(1);
        w.WriteBytes(publicKey);
        w.WriteBytes(signature);
    }

    public static (byte[]? PublicKey, byte[]? Signature) Read(ref CodexReader r)
    {
        var present = r.ReadByte();
        return present switch
        {
            0 => (null, null),
            1 => (r.ReadBytes().ToArray(), r.ReadBytes().ToArray()),
            _ => throw new CodexFormatException("Invalid author-envelope presence flag."),
        };
    }
}

/// <summary>
/// The signing half of a rite's authenticated-authorship envelope: the author's long-term Seal keys.
/// This is deliberately a Rites-local shape (raw key bytes) so the rite layer need not depend on the
/// identity/Core types. The public key may be a per-channel key distinct from the overlay Sigil.
/// </summary>
public sealed record RiteIdentity(byte[] SealPublicKey, byte[] SealPrivateKey);

/// <summary>
/// The optional authenticated-authorship envelope shared by all three rites (Epistle, Conduit, Reliquary).
/// When present it carries the author's Seal public key plus an Ed25519 signature over a domain-separated
/// digest of the rite's canonical content. Because the channel key only proves <em>membership</em> — not
/// <em>authorship</em> — a relaying member (or the transport) could otherwise forge or rewrite the author
/// of a message that is passed peer-to-peer. This envelope closes that: the signature binds the author's
/// Sigil to the exact content (including its MessageId / TransferId), so it cannot be transplanted, and a
/// re-broadcast under a fresh id fails verification. The digest is domain-separated per rite so a signature
/// minted for one rite can never be replayed as another.
/// </summary>
public static class RiteAuthor
{
    private const string DomainPrefix = "cuprinet/rite/";

    /// <summary>Maximum accepted Seal public-key length (matches the checks elsewhere in the codebase).</summary>
    public const int MaxSealPublicKey = 64;

    /// <summary>Signs <paramref name="content"/> for a rite <paramref name="domain"/>, returning the envelope.</summary>
    public static (byte[] PublicKey, byte[] Signature) Sign(
        string domain, ReadOnlySpan<byte> content, RiteIdentity author, ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(suite);
        var signer = suite.CreateSigner(author.SealPrivateKey);
        var signature = signer.Sign(Digest(domain, content));
        return (author.SealPublicKey, signature);
    }

    /// <summary>
    /// Verifies an author envelope against <paramref name="content"/>. An <em>absent</em> envelope (both
    /// fields null) is acceptable only when <paramref name="requireAuthor"/> is false. A present-but-invalid
    /// envelope (bad length or failed signature) is always rejected.
    /// </summary>
    public static bool Verify(
        string domain, ReadOnlySpan<byte> content, byte[]? publicKey, byte[]? signature,
        ICryptoSuite suite, bool requireAuthor)
    {
        ArgumentNullException.ThrowIfNull(suite);
        if (publicKey is null && signature is null)
            return !requireAuthor;
        if (publicKey is null || signature is null)
            return false;
        if (publicKey.Length is 0 or > MaxSealPublicKey || signature.Length == 0)
            return false;
        return suite.Verifier.Verify(Digest(domain, content), signature, publicKey);
    }

    /// <summary>The author's Sigil, derived from an envelope's Seal public key.</summary>
    public static Sigil AuthorSigil(byte[] publicKey) => Sigil.FromSealPublicKey(publicKey);

    private static byte[] Digest(string domain, ReadOnlySpan<byte> content)
    {
        var prefix = Encoding.UTF8.GetBytes(DomainPrefix + domain + "\0");
        var buffer = new byte[prefix.Length + content.Length];
        prefix.CopyTo(buffer, 0);
        content.CopyTo(buffer.AsSpan(prefix.Length));
        return SHA256.HashData(buffer);
    }
}
