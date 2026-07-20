using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;

namespace CupriNet.Core;

/// <summary>A per-relationship key pair. Each neighbour sees a different Guise, so relationships are
/// not trivially linkable. (In the secure suite this becomes an X25519 agreement key.)</summary>
public sealed record GuiseKeyPair(byte[] PrivateKey, byte[] PublicKey);

/// <summary>
/// Binds a Guise public key to the node's long-term Seal: the Seal signs (peerSigil, guisePublicKey), so
/// a peer can confirm the Guise it is talking to really belongs to the claimed node identity.
/// </summary>
public static class GuiseBinding
{
    private const string Context = "cuprinet/guise-binding/v1";

    /// <summary>Produces the canonical bytes that a Guise binding signs.</summary>
    public static byte[] BindingBytes(Sigil peerSigil, ReadOnlySpan<byte> guisePublicKey)
    {
        var w = new CodexWriter();
        w.WriteString(Context);
        w.WriteBytes(peerSigil.Span);
        w.WriteBytes(guisePublicKey);
        return w.ToArray();
    }

    /// <summary>Signs a Guise binding with the node's Seal.</summary>
    public static byte[] Create(NodeIdentity identity, Sigil peerSigil, ReadOnlySpan<byte> guisePublicKey, ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(suite);
        var signer = suite.CreateSigner(identity.Seal.PrivateKey);
        return signer.Sign(BindingBytes(peerSigil, guisePublicKey));
    }

    /// <summary>Verifies a Guise binding against the signer's Seal public key.</summary>
    public static bool Verify(ICryptoSuite suite, ReadOnlySpan<byte> sealPublicKey, Sigil peerSigil, ReadOnlySpan<byte> guisePublicKey, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(suite);
        return suite.Verifier.Verify(BindingBytes(peerSigil, guisePublicKey), signature, sealPublicKey);
    }
}
