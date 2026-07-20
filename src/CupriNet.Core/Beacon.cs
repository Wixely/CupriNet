namespace CupriNet.Core;

/// <summary>The kind of an endpoint candidate, in rough order of connection preference.</summary>
public enum EndpointKind : byte
{
    /// <summary>A local (LAN) address observed by the host itself.</summary>
    Host = 0,

    /// <summary>An externally observed (server-reflexive / mapped) address.</summary>
    Mapped = 1,

    /// <summary>An explicitly configured hostname or address.</summary>
    Manual = 2,

    /// <summary>A relay (Ferryman) candidate — L1 data / L2 coordination only.</summary>
    Relay = 3,
}

/// <summary>
/// A single reachability candidate carried inside an Intonation. Beacons are attempted in priority
/// order during the connection procedure.
/// </summary>
public sealed record Beacon(EndpointKind Kind, string Host, int Port);
