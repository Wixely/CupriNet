using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Marks;

namespace CupriNet.Core;

/// <summary>The outcome of validating an Intonation.</summary>
public enum IntonationStatus
{
    /// <summary>Signature valid, network matches, version supported, not yet severed.</summary>
    Valid,

    /// <summary>The document could not be parsed.</summary>
    Malformed,

    /// <summary>The version is not supported by this build.</summary>
    UnsupportedVersion,

    /// <summary>The Intonation belongs to a different network.</summary>
    WrongNetwork,

    /// <summary>The signature did not verify against the embedded Seal public key.</summary>
    BadSignature,

    /// <summary>The Intonation has severed (expired). The caller may still choose to attempt it.</summary>
    Severed,
}

/// <summary>The result of validating an Intonation, including the parsed value when available.</summary>
public sealed record IntonationValidation(IntonationStatus Status, Intonation? Intonation)
{
    public bool IsValid => Status == IntonationStatus.Valid;
}

/// <summary>
/// Validates an Intonation: parse, version, network, signature (via the Alembic seam), then expiry.
/// Expiry is reported distinctly (Severed) so a caller may still choose to attempt an old link.
/// </summary>
public static class IntonationValidator
{
    /// <summary>Validates a connection URL against the expected network at the given moment.</summary>
    public static IntonationValidation ValidateUri(string uri, Concordium expectedNetwork, ICryptoSuite suite, DateTimeOffset now)
    {
        if (!IntonationUri.TryParse(uri, out var intonation, out var signedBody))
            return new IntonationValidation(IntonationStatus.Malformed, null);
        return Validate(intonation, signedBody, expectedNetwork, suite, now);
    }

    /// <summary>Validates a raw document buffer against the expected network at the given moment.</summary>
    public static IntonationValidation ValidateDocument(ReadOnlySpan<byte> document, Concordium expectedNetwork, ICryptoSuite suite, DateTimeOffset now)
    {
        Intonation intonation;
        byte[] signedBody;
        try
        {
            (intonation, signedBody) = IntonationCodec.Decode(document);
        }
        catch (Codex.CodexFormatException)
        {
            return new IntonationValidation(IntonationStatus.Malformed, null);
        }

        return Validate(intonation, signedBody, expectedNetwork, suite, now);
    }

    private static IntonationValidation Validate(Intonation intonation, ReadOnlySpan<byte> signedBody, Concordium expectedNetwork, ICryptoSuite suite, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(suite);

        // Range-accept the link version (CupriMark) instead of an exact-equality check: a version we support,
        // at or above our security floor, and not buried. A future security bump raises the floor and refuses
        // old links; a version newer than we understand is refused too (we can't parse it safely).
        if (!CupriMarks.Accepts(CupriMarks.Intonation, intonation.Version))
            return new IntonationValidation(IntonationStatus.UnsupportedVersion, intonation);

        if (intonation.Network != expectedNetwork)
            return new IntonationValidation(IntonationStatus.WrongNetwork, intonation);

        if (!suite.Verifier.Verify(signedBody, intonation.Signature, intonation.InviterSealPublicKey))
            return new IntonationValidation(IntonationStatus.BadSignature, intonation);

        if (now.ToUnixTimeSeconds() > intonation.SeveranceUnix)
            return new IntonationValidation(IntonationStatus.Severed, intonation);

        return new IntonationValidation(IntonationStatus.Valid, intonation);
    }
}
