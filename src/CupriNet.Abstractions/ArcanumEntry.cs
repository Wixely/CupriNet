namespace CupriNet.Abstractions;

/// <summary>
/// Access mode of an Arcanum (channel), governing how a peer may enter.
/// </summary>
public enum ArcanumEntry
{
    /// <summary>Anyone holding the Watchword may enter (OpenSecret).</summary>
    Aperture = 0,

    /// <summary>Watchword grants a lobby; permanent membership needs an owner Investiture (OwnerApproved).</summary>
    Sanction = 1,

    /// <summary>Entry requires an owner-issued Investiture (InviteOnly).</summary>
    Sealed = 2,

    /// <summary>No owner; membership rests on the Watchword alone (Ownerless).</summary>
    Anarch = 3,
}
