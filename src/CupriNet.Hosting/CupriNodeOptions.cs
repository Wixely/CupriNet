using System.Net;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Core;

namespace CupriNet.Hosting;

/// <summary>Configuration for a <see cref="CupriNode"/>.</summary>
public sealed record CupriNodeOptions
{
    /// <summary>The network (Concordance) this node belongs to.</summary>
    public required string Concordium { get; init; }

    /// <summary>TCP port to listen on (0 = OS-assigned ephemeral port).</summary>
    public int ListenPort { get; init; }

    /// <summary>Address to bind. Defaults to loopback; use <see cref="IPAddress.Any"/> for all interfaces.</summary>
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;

    /// <summary>
    /// Crypto suite. Defaults to the secure BouncyCastle suite; pass the Simulacrum (with consent) only
    /// for development.
    /// </summary>
    public ICryptoSuite? Suite { get; init; }

    /// <summary>Secret store for identity/relationships. Defaults to in-memory (non-persistent).</summary>
    public ISecretStore? SecretStore { get; init; }

    /// <summary>Reachability candidates to advertise in Intonations. Defaults to the bound endpoint.</summary>
    public IReadOnlyList<Beacon>? AdvertisedBeacons { get; init; }

    /// <summary>
    /// Run a reflexive-endpoint exchange during pairing so the node learns its externally-observed
    /// (Mapped) address. Requires both peers to support it. Default true.
    /// </summary>
    public bool EnableReflexiveDiscovery { get; init; } = true;
}
