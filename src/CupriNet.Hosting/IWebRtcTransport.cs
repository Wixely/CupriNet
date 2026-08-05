using CupriNet.Core;
using CupriNet.Vessel;

namespace CupriNet.Hosting;

/// <summary>
/// The seam CupriNet uses to accept browser WebRTC DataChannel clients without depending on a specific WebRTC
/// implementation. An app supplies a concrete transport (e.g. one backed by the CupriWebRTC package) via
/// <see cref="CupriNodeOptions.WebRtcTransport"/>; the node advertises its static <see cref="Parameters"/> in every
/// Intonation (so a browser can dial it with no signalling server) and, when a browser opens a DataChannel, the
/// transport raises <see cref="ChannelAccepted"/> with the channel wrapped as an <see cref="IVessel"/>. The node runs
/// the ordinary Toll + Noise + mux + Consecration stack over it via <see cref="CupriNode.AcceptChannelOverVesselAsync"/>
/// and surfaces the peer through the normal <see cref="CupriNode.AcceptAsync"/> queue — so a browser client is just
/// another peer. Keeping this an interface means <c>CupriNet.Hosting</c> never references a WebRTC library directly.
/// </summary>
public interface IWebRtcTransport : IAsyncDisposable
{
    /// <summary>
    /// The static parameters (ICE-lite ufrag/pwd, DTLS fingerprint, UDP port) to advertise in this node's link, so a
    /// browser can preload them as the remote description and dial the endpoint directly. The host address(es) come
    /// from the Intonation's beacons; this block adds the WebRTC-specific bits.
    /// </summary>
    WebRtcEndpoint Parameters { get; }

    /// <summary>Raised when a browser opens a DataChannel, carrying it as a connected, inbound <see cref="IVessel"/>.</summary>
    event Action<IVessel>? ChannelAccepted;

    /// <summary>Starts listening for inbound WebRTC connections (binds the UDP socket, runs ICE/DTLS/SCTP).</summary>
    Task StartAsync(CancellationToken cancellationToken = default);
}
