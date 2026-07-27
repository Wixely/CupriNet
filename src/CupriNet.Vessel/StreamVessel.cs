using System.Net;
using CupriNet.Codex;

namespace CupriNet.Vessel;

/// <summary>
/// Wraps any duplex byte <see cref="Stream"/> as a framed, multiplexed <see cref="Vessel"/>. This is the public
/// seam every stream-based transport uses to hand CupriNet a vessel — a Tor onion stream, a WebSocket, a TLS
/// stream — after which the ordinary Noise + mux + pairing stack runs over it unchanged. (The raw <see cref="Vessel"/>
/// constructor is internal; TCP and reliable-UDP use their own factories, and this is the one for everything else.)
/// </summary>
public static class StreamVessel
{
    /// <summary>Builds a Vessel over <paramref name="stream"/>. <paramref name="onDispose"/> runs after the stream
    /// is disposed (e.g. to release the underlying transport). Endpoints are advisory metadata only.</summary>
    public static Vessel Over(
        Stream stream,
        EndPoint? localEndPoint = null,
        EndPoint? remoteEndPoint = null,
        int maxFrameSize = FrameCodec.DefaultMaxFrameSize,
        Func<ValueTask>? onDispose = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new Vessel(stream, localEndPoint, remoteEndPoint, maxFrameSize, onDispose);
    }
}
