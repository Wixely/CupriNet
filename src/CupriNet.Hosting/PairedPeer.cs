using CupriNet.Abstractions;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.Hosting;

/// <summary>
/// A transport-paired peer: an authenticated Vessel established by a Conjunction handshake. Consecrate a
/// channel over it to obtain an <see cref="ArcanumSession"/>. Disposing it closes the connection.
/// </summary>
public sealed class PairedPeer : IAsyncDisposable
{
    internal PairedPeer(VesselSession vessel, Sigil peerSigil, byte[] peerSealPublicKey, bool isInitiator)
    {
        Vessel = vessel;
        PeerSigil = peerSigil;
        PeerSealPublicKey = peerSealPublicKey;
        IsInitiator = isInitiator;
    }

    internal VesselSession Vessel { get; }

    /// <summary>The authenticated peer's Sigil.</summary>
    public Sigil PeerSigil { get; }

    /// <summary>The authenticated peer's Seal public key.</summary>
    public byte[] PeerSealPublicKey { get; }

    /// <summary>True if this node dialled out (and so should initiate the Consecration).</summary>
    public bool IsInitiator { get; }

    public ValueTask DisposeAsync() => Vessel.DisposeAsync();
}
