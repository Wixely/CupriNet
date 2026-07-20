using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Codex;

namespace CupriNet.Arcanum;

/// <summary>Roles a member may hold within an Arcanum.</summary>
[Flags]
public enum ArcanumRole : uint
{
    None = 0,
    Member = 1,
    Steward = 2,  // delegated moderator
    Magister = 4, // owner
}

/// <summary>Thrown when an ownership document or chain is invalid.</summary>
public sealed class OwnershipException(string message) : Exception(message);

/// <summary>
/// The founding, owner-signed root of an Arcanum's ownership (Reign 0). Honest clients accept only
/// channel state authorised by the owner chain that starts here. The ChannelId binds it to a specific
/// channel (its Ascendant), so a descriptor cannot be replayed onto another Arcanum.
/// </summary>
public sealed record ChannelDescriptor
{
    public required byte Version { get; init; }
    public required byte[] ChannelId { get; init; }
    public required byte[] OwnerPublicKey { get; init; }
    public required ArcanumEntry AccessMode { get; init; }
    public required long CreatedAtUnix { get; init; }
    public required uint PolicyVersion { get; init; }
    public required byte[] Signature { get; init; }

    public Sigil OwnerSigil => Sigil.FromSealPublicKey(OwnerPublicKey);
}

/// <summary>
/// An owner-issued membership credential. Knowledge of the Watchword grants discovery/lobby access; an
/// Investiture grants authorised membership — so a leaked Watchword alone cannot confer permanent access.
/// </summary>
public sealed record Investiture
{
    public required byte Version { get; init; }
    public required byte[] ChannelId { get; init; }
    public required byte[] MemberSealPublicKey { get; init; }
    public required ArcanumRole Roles { get; init; }
    public required long NotBeforeUnix { get; init; }
    public required long NotAfterUnix { get; init; }
    public required ulong SerialNumber { get; init; }
    public required byte[] IssuerPublicKey { get; init; }
    public required byte[] Signature { get; init; }

    public Sigil MemberSigil => Sigil.FromSealPublicKey(MemberSealPublicKey);
}

/// <summary>
/// A signed link in the ownership chain: the previous owner transfers to a new owner at the next Reign,
/// chained by the hash of the previous document. Conflicting links at the same Reign are a Schism (fork),
/// never resolved by timestamp.
/// </summary>
public sealed record AscensionLink
{
    public required byte[] ChannelId { get; init; }
    public required ulong Reign { get; init; }
    public required byte[] NewOwnerPublicKey { get; init; }
    public required byte[] PreviousHash { get; init; }
    public required byte[] Signature { get; init; }
}

/// <summary>The resolved head of an ownership chain.</summary>
public sealed record OwnershipState(byte[] CurrentOwnerPublicKey, ulong Reign);

/// <summary>Serialization, signing, verification, and chain resolution for Arcanum ownership.</summary>
public static class Ownership
{
    public const byte DescriptorVersion = 1;
    public const byte InvestitureVersion = 1;

    /// <summary>The ChannelId for a channel is its Ascendant.</summary>
    public static byte[] ChannelId(ArcanumKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return keys.Ascendant.Span.ToArray();
    }

    // ---- Descriptor ----------------------------------------------------------------------------

    public static byte[] DescriptorBody(ChannelDescriptor d)
    {
        var w = new CodexWriter();
        w.WriteByte(d.Version);
        w.WriteBytes(d.ChannelId);
        w.WriteBytes(d.OwnerPublicKey);
        w.WriteUInt32((uint)d.AccessMode);
        w.WriteUInt64((ulong)d.CreatedAtUnix);
        w.WriteUInt32(d.PolicyVersion);
        return w.ToArray();
    }

    public static ChannelDescriptor CreateDescriptor(SealKeyPair owner, byte[] channelId, ArcanumEntry accessMode, uint policyVersion, ICryptoSuite suite, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(channelId);
        ArgumentNullException.ThrowIfNull(suite);

        var draft = new ChannelDescriptor
        {
            Version = DescriptorVersion,
            ChannelId = channelId,
            OwnerPublicKey = owner.PublicKey,
            AccessMode = accessMode,
            CreatedAtUnix = now.ToUnixTimeSeconds(),
            PolicyVersion = policyVersion,
            Signature = [],
        };
        var signature = suite.CreateSigner(owner.PrivateKey).Sign(DescriptorBody(draft));
        return draft with { Signature = signature };
    }

    public static bool VerifyDescriptor(ChannelDescriptor d, ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(suite);
        return suite.Verifier.Verify(DescriptorBody(d), d.Signature, d.OwnerPublicKey);
    }

    // ---- Investiture ---------------------------------------------------------------------------

    public static byte[] InvestitureBody(Investiture inv)
    {
        var w = new CodexWriter();
        w.WriteByte(inv.Version);
        w.WriteBytes(inv.ChannelId);
        w.WriteBytes(inv.MemberSealPublicKey);
        w.WriteUInt32((uint)inv.Roles);
        w.WriteUInt64((ulong)inv.NotBeforeUnix);
        w.WriteUInt64((ulong)inv.NotAfterUnix);
        w.WriteUInt64(inv.SerialNumber);
        w.WriteBytes(inv.IssuerPublicKey);
        return w.ToArray();
    }

    public static Investiture Invest(SealKeyPair issuer, byte[] channelId, byte[] memberSealPublicKey, ArcanumRole roles, DateTimeOffset notBefore, DateTimeOffset notAfter, ulong serialNumber, ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(channelId);
        ArgumentNullException.ThrowIfNull(memberSealPublicKey);
        ArgumentNullException.ThrowIfNull(suite);

        var draft = new Investiture
        {
            Version = InvestitureVersion,
            ChannelId = channelId,
            MemberSealPublicKey = memberSealPublicKey,
            Roles = roles,
            NotBeforeUnix = notBefore.ToUnixTimeSeconds(),
            NotAfterUnix = notAfter.ToUnixTimeSeconds(),
            SerialNumber = serialNumber,
            IssuerPublicKey = issuer.PublicKey,
            Signature = [],
        };
        var signature = suite.CreateSigner(issuer.PrivateKey).Sign(InvestitureBody(draft));
        return draft with { Signature = signature };
    }

    /// <summary>Verifies an Investiture: signed by the expected issuer, for this channel, and currently valid.</summary>
    public static bool VerifyInvestiture(Investiture inv, byte[] expectedIssuerPublicKey, byte[] channelId, ICryptoSuite suite, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inv);
        ArgumentNullException.ThrowIfNull(expectedIssuerPublicKey);
        ArgumentNullException.ThrowIfNull(channelId);
        ArgumentNullException.ThrowIfNull(suite);

        if (!inv.ChannelId.AsSpan().SequenceEqual(channelId))
            return false;
        if (!inv.IssuerPublicKey.AsSpan().SequenceEqual(expectedIssuerPublicKey))
            return false;

        var nowUnix = now.ToUnixTimeSeconds();
        if (nowUnix < inv.NotBeforeUnix || nowUnix > inv.NotAfterUnix)
            return false;

        return suite.Verifier.Verify(InvestitureBody(inv), inv.Signature, inv.IssuerPublicKey);
    }

    // ---- Ascension chain -----------------------------------------------------------------------

    public static byte[] AscensionBody(AscensionLink link)
    {
        var w = new CodexWriter();
        w.WriteBytes(link.ChannelId);
        w.WriteUInt64(link.Reign);
        w.WriteBytes(link.NewOwnerPublicKey);
        w.WriteBytes(link.PreviousHash);
        return w.ToArray();
    }

    /// <summary>The previous owner signs a transfer to a new owner at the next Reign.</summary>
    public static AscensionLink Ascend(SealKeyPair previousOwner, byte[] channelId, ulong newReign, byte[] newOwnerPublicKey, byte[] previousHash, ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(channelId);
        ArgumentNullException.ThrowIfNull(newOwnerPublicKey);
        ArgumentNullException.ThrowIfNull(previousHash);
        ArgumentNullException.ThrowIfNull(suite);

        var draft = new AscensionLink
        {
            ChannelId = channelId,
            Reign = newReign,
            NewOwnerPublicKey = newOwnerPublicKey,
            PreviousHash = previousHash,
            Signature = [],
        };
        var signature = suite.CreateSigner(previousOwner.PrivateKey).Sign(AscensionBody(draft));
        return draft with { Signature = signature };
    }

    public static byte[] HashOf(ChannelDescriptor d, ICryptoSuite suite) => suite.Hash.Sha256(DescriptorBody(d));

    public static byte[] HashOf(AscensionLink link, ICryptoSuite suite) => suite.Hash.Sha256(AscensionBody(link));

    /// <summary>
    /// Validates a linear ownership chain and returns its head. Throws on a broken hash link, a
    /// non-monotonic Reign, a channel mismatch, or a transfer not signed by the reigning owner.
    /// </summary>
    public static OwnershipState Resolve(ChannelDescriptor descriptor, IReadOnlyList<AscensionLink> links, ICryptoSuite suite)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(suite);

        if (!VerifyDescriptor(descriptor, suite))
            throw new OwnershipException("Channel descriptor signature is invalid.");

        var currentOwner = descriptor.OwnerPublicKey;
        var currentHash = HashOf(descriptor, suite);
        ulong reign = 0;

        foreach (var link in links)
        {
            if (link.Reign != reign + 1)
                throw new OwnershipException($"Non-monotonic Reign: expected {reign + 1}, got {link.Reign}.");
            if (!link.ChannelId.AsSpan().SequenceEqual(descriptor.ChannelId))
                throw new OwnershipException("Ascension link is for a different channel.");
            if (!link.PreviousHash.AsSpan().SequenceEqual(currentHash))
                throw new OwnershipException("Ascension link does not chain to the previous document.");
            if (!suite.Verifier.Verify(AscensionBody(link), link.Signature, currentOwner))
                throw new OwnershipException("Ascension link is not signed by the reigning owner.");

            currentOwner = link.NewOwnerPublicKey;
            currentHash = HashOf(link, suite);
            reign = link.Reign;
        }

        return new OwnershipState(currentOwner, reign);
    }

    /// <summary>
    /// Detects a Schism: two individually-valid ownership branches from the same descriptor that reach
    /// the same Reign with different owners. Honest clients on each side follow their own branch — this
    /// method reports the fork rather than choosing a winner.
    /// </summary>
    public static bool IsSchism(ChannelDescriptor descriptor, IReadOnlyList<AscensionLink> branchA, IReadOnlyList<AscensionLink> branchB, ICryptoSuite suite)
    {
        var a = Resolve(descriptor, branchA, suite);
        var b = Resolve(descriptor, branchB, suite);
        return a.Reign == b.Reign && !a.CurrentOwnerPublicKey.AsSpan().SequenceEqual(b.CurrentOwnerPublicKey);
    }
}
