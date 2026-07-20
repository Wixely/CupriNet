namespace CupriNet.Alembic;

/// <summary>
/// The Alembic: the single seam through which all cryptography flows. Callers depend only on this
/// facade, never on a concrete provider — so the insecure Simulacrum used during early development
/// can be swapped for the BouncyCastle-backed secure suite (the Tempering) without touching callers.
/// </summary>
public interface ICryptoSuite
{
    /// <summary>Human-readable suite name (e.g. "Simulacrum", "BouncyCastle").</summary>
    string Name { get; }

    /// <summary>
    /// <c>false</c> for development-only suites that provide no real protection. Production hosts
    /// must refuse to start on an insecure suite unless insecure operation is explicitly consented to.
    /// </summary>
    bool IsSecure { get; }

    IAead Aead { get; }
    IHashProvider Hash { get; }
    IKdf Kdf { get; }
    IPasswordHardener Passwords { get; }
    IVerifier Verifier { get; }

    /// <summary>Diffie–Hellman key agreement (X25519). May be unsupported in insecure development suites.</summary>
    IKeyAgreement Agreement { get; }

    /// <summary>Creates a signer bound to the given private Seal.</summary>
    ISigner CreateSigner(ReadOnlySpan<byte> privateSeal);

    /// <summary>Generates a fresh long-term identity (Seal) key pair.</summary>
    SealKeyPair GenerateSeal();
}
