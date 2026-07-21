using System.Net;
using System.Net.Sockets;
using CupriNet.Codex;

namespace CupriNet.Vessel;

/// <summary>Accepts inbound TCP connections and surfaces them as <see cref="Vessel"/> sessions.</summary>
public sealed class VesselListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly int _maxFrameSize;

    public VesselListener(IPEndPoint endpoint, int maxFrameSize = FrameCodec.DefaultMaxFrameSize)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _listener = new TcpListener(endpoint);
        _maxFrameSize = maxFrameSize;
    }

    /// <summary>The bound local endpoint (valid after <see cref="Start"/>; reflects the OS-assigned port when port 0 was requested).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public void Start() => _listener.Start();

    public async Task<Vessel> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        client.NoDelay = true;
        return FromTcp(client, _maxFrameSize);
    }

    internal static Vessel FromTcp(TcpClient client, int maxFrameSize)
        => new(client.GetStream(), client.Client.LocalEndPoint, client.Client.RemoteEndPoint, maxFrameSize,
            () => { client.Dispose(); return ValueTask.CompletedTask; });

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Establishes outbound Vessel connections over TCP.</summary>
public static class TcpVessel
{
    public static async Task<Vessel> ConnectAsync(string host, int port, int maxFrameSize = FrameCodec.DefaultMaxFrameSize, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return VesselListener.FromTcp(client, maxFrameSize);
    }
}

/// <summary>Builds a Vessel over reliable UDP: a <see cref="ReliableArq"/>/<see cref="ArqStream"/> beneath the
/// same framing the TCP path uses, so Noise and the mux run over a UDP (e.g. hole-punched) path unchanged.</summary>
public static class UdpVessel
{
    public static Vessel Over(IPacketLink link, int maxFrameSize = FrameCodec.DefaultMaxFrameSize)
    {
        ArgumentNullException.ThrowIfNull(link);
        var stream = new ArqStream(link); // ArqStream owns the link and disposes it
        return new Vessel(stream, link.LocalEndPoint, link.RemoteEndPoint, maxFrameSize);
    }
}
