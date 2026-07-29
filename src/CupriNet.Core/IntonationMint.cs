using System.Security.Cryptography;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Marks;

namespace CupriNet.Core;

/// <summary>Options controlling a minted Intonation.</summary>
public sealed record IntonationOptions
{
    /// <summary>The network the Intonation is scoped to.</summary>
    public required Concordium Network { get; init; }

    /// <summary>The inviter's reachability candidates.</summary>
    public IReadOnlyList<Beacon> Beacons { get; init; } = [];

    /// <summary>A sampled roll of seed peer Sigils (overlay entry points).</summary>
    public IReadOnlyList<Sigil> Litany { get; init; } = [];

    /// <summary>How long before the Intonation severs (expires).</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Optional capability secret bound into the link.</summary>
    public byte[]? Petition { get; init; }
}

/// <summary>Mints signed Intonations for a node identity (the <c>Intone</c> command).</summary>
public static class IntonationMint
{
    private const int NonceSize = 16;

    /// <summary>Intones a fresh, signed connection URL document snapshotting the node's current state.</summary>
    public static Intonation Intone(NodeIdentity identity, ICryptoSuite suite, IntonationOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(options);

        var draft = new Intonation
        {
            // Stamp the highest link version this build supports, per the CupriMark catalogue (the single
            // source of versioning truth); recipients accept it via CupriMarks.Accepts in IntonationValidator.
            Version = checked((byte)CupriMarks.Supported(CupriMarks.Intonation).Max),
            Network = options.Network,
            InviterSealPublicKey = identity.PublicKey.ToArray(),
            Beacons = options.Beacons,
            Litany = options.Litany,
            IssuedAtUnix = now.ToUnixTimeSeconds(),
            SeveranceUnix = now.Add(options.Lifetime).ToUnixTimeSeconds(),
            Nonce = RandomNumberGenerator.GetBytes(NonceSize),
            Petition = options.Petition,
            Signature = [],
        };

        var body = IntonationCodec.EncodeBody(draft);
        var signer = suite.CreateSigner(identity.Seal.PrivateKey);
        var signature = signer.Sign(body);

        return draft with { Signature = signature };
    }
}
