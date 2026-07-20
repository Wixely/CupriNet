using CupriNet.Abstractions;
using CupriNet.Alembic;

namespace CupriNet.Core;

/// <summary>
/// A node's long-term identity: its Seal (signing key pair) and the derived Sigil. In Layer 1 this is
/// a pseudonym, not linked to any real-world identity.
/// </summary>
public sealed class NodeIdentity
{
    private NodeIdentity(SealKeyPair seal)
    {
        Seal = seal;
        Sigil = Sigil.FromSealPublicKey(seal.PublicKey);
    }

    /// <summary>The long-term signing key pair.</summary>
    public SealKeyPair Seal { get; }

    /// <summary>The public mark derived from the Seal public key.</summary>
    public Sigil Sigil { get; }

    /// <summary>The Seal public key (safe to share).</summary>
    public ReadOnlyMemory<byte> PublicKey => Seal.PublicKey;

    /// <summary>Generates a brand-new identity using the given crypto suite.</summary>
    public static NodeIdentity Generate(ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(suite);
        return new NodeIdentity(suite.GenerateSeal());
    }

    /// <summary>Rehydrates an identity from a persisted Seal key pair.</summary>
    public static NodeIdentity FromSeal(SealKeyPair seal) => new(seal);
}
