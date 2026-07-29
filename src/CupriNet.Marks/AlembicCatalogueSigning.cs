using CupriMark.Signing;
using CupriNet.Alembic;

namespace CupriNet.Marks;

/// <summary>
/// Bridges CupriNet's Alembic crypto seam to CupriMark's signing abstraction, so a catalogue can be
/// release-signed and verified with the node's own Ed25519 (the Seal) — no second Ed25519 implementation
/// and no dependency on CupriMark's optional signing companion. CupriMark hands over the exact canonical
/// bytes; these adapters route them through <see cref="ICryptoSuite"/>.
/// </summary>
public sealed class AlembicCatalogueSigner : ICatalogueSigner
{
    private readonly ISigner _signer;
    private readonly byte[] _publicKey;

    /// <summary>Creates a catalogue signer over an Alembic Seal key pair.</summary>
    public AlembicCatalogueSigner(ICryptoSuite suite, ReadOnlySpan<byte> privateSeal, ReadOnlySpan<byte> publicSeal)
    {
        ArgumentNullException.ThrowIfNull(suite);
        _signer = suite.CreateSigner(privateSeal);
        _publicKey = publicSeal.ToArray();
    }

    /// <inheritdoc/>
    public ReadOnlySpan<byte> PublicKey => _publicKey;

    /// <inheritdoc/>
    public byte[] Sign(ReadOnlySpan<byte> canonicalBody) => _signer.Sign(canonicalBody);
}

/// <summary>
/// Verifies a catalogue signature through the Alembic seam. Note the argument-order remap: CupriMark's
/// <see cref="ICatalogueVerifier.Verify"/> is <c>(body, publicKey, signature)</c> whereas Alembic's
/// <see cref="IVerifier.Verify"/> is <c>(message, signature, publicKey)</c>.
/// </summary>
public sealed class AlembicCatalogueVerifier : ICatalogueVerifier
{
    private readonly IVerifier _verifier;

    /// <summary>Creates a catalogue verifier over an Alembic crypto suite.</summary>
    public AlembicCatalogueVerifier(ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(suite);
        _verifier = suite.Verifier;
    }

    /// <inheritdoc/>
    public bool Verify(ReadOnlySpan<byte> canonicalBody, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> signature)
        => _verifier.Verify(canonicalBody, signature, publicKey);
}
