using CupriNet.Arcanum;
using CupriNet.Rites;

namespace CupriNet.Hosting;

/// <summary>Options for <c>ConsecrateAsync</c> that keep the overlay (L1) and channel (L2) layers separate.</summary>
public sealed record ConsecrateOptions
{
    /// <summary>
    /// The channel persona to speak and hold membership under — a Seal keypair <em>distinct</em> from this
    /// node's overlay identity. Using a persona means the node's channel authorship and membership are
    /// attributed to the persona, unlinkable to the overlay Sigil it cultivates the network under. When
    /// null, the overlay identity is used and there is no L1/L2 separation.
    /// </summary>
    public RiteIdentity? ChannelIdentity { get; init; }

    /// <summary>Credential context for a gated channel (see <see cref="ArcanumAdmission"/>). Null for open channels.</summary>
    public ArcanumAdmission? Admission { get; init; }
}

/// <summary>
/// The credential context for joining a gated channel. Knowing the Watchword derives the channel key
/// (discovery + transport), but for an owner-gated access mode (<see cref="ArcanumEntry.Sanction"/> /
/// <see cref="ArcanumEntry.Sealed"/>) admission additionally requires proof of membership: either being
/// the channel owner, or presenting an <see cref="Investiture"/> the owner signed for this node. Supply
/// this to <c>ConsecrateAsync</c> to enforce (and to present our own credential to the peer). Omit it —
/// or use an <see cref="ArcanumEntry.Aperture"/> / <see cref="ArcanumEntry.Anarch"/> descriptor — for an
/// open channel where the Watchword alone is membership.
/// </summary>
public sealed record ArcanumAdmission
{
    /// <summary>The owner-signed channel descriptor (owner key + access mode). Must match the Watchword's channel.</summary>
    public required ChannelDescriptor Descriptor { get; init; }

    /// <summary>Our own membership credential to present. May be null when this node is the channel owner.</summary>
    public Investiture? MyInvestiture { get; init; }
}
