using CupriNet.Arcanum;

namespace CupriNet.Hosting;

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
