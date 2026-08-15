using CupriNet.Abstractions;
using CupriNet.Alembic;

namespace CupriNet.Core;

/// <summary>
/// A Shrine's self-authenticating identity: an Ed25519 Seal key pair distinct from the node's overlay Seal/Sigil,
/// so hosting a Shrine leaks no Layer-1 identity. Its public form — the <see cref="Address"/> (<c>cupri1…</c>) — is
/// what a Pilgrim dials and pins during the Pilgrimage. "The address is the key": it is the bech32 fingerprint of
/// the Signet's own Seal, proven by the handshake, not an entry in any registry.
/// </summary>
public sealed class Signet
{
    private Signet(SealKeyPair seal)
    {
        Seal = seal;
        Sigil = Sigil.FromSealPublicKey(seal.PublicKey);
        Address = Bech32.Fingerprint(Sigil);
    }

    /// <summary>The Shrine's signing key pair.</summary>
    public SealKeyPair Seal { get; }

    /// <summary>The mark derived from the Signet public key (distinct from the node's own Sigil).</summary>
    public Sigil Sigil { get; }

    /// <summary>The public <c>cupri1…</c> address a Pilgrim dials and pins.</summary>
    public string Address { get; }

    /// <summary>The Signet public key (safe to share).</summary>
    public ReadOnlyMemory<byte> PublicKey => Seal.PublicKey;

    /// <summary>Generates a brand-new Signet using the given crypto suite.</summary>
    public static Signet Generate(ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(suite);
        return new Signet(suite.GenerateSeal());
    }

    /// <summary>Rehydrates a Signet from a persisted Seal key pair.</summary>
    public static Signet FromSeal(SealKeyPair seal) => new(seal);
}
