using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CupriNet.Core;

namespace CupriNet.Traversal;

/// <summary>A NAT port mapping obtained from the gateway: the external port the internal one is reachable on.</summary>
public sealed record PortMapping(int InternalPort, int ExternalPort, TimeSpan Lifetime);

/// <summary>
/// A minimal NAT-PMP client (RFC 6886): asks the home gateway to map an external port to this node's listen port,
/// and to report the external IP — so a node behind a cooperating router becomes directly reachable with no manual
/// port-forwarding, yielding a real <see cref="EndpointKind.Mapped"/> beacon (correct IP <em>and</em> port, unlike
/// an HTTP "what's my IP" guess). Pure managed, no native dependency. Encoders/parsers are public so the wire
/// format is unit-testable against an in-process mock gateway.
/// </summary>
public static class NatPmp
{
    public const int GatewayPort = 5351;
    private const byte Version = 0;
    private const byte OpExternalAddress = 0;
    private const byte OpMapUdp = 1;
    private const byte OpMapTcp = 2;
    private const byte ResponseFlag = 0x80;

    // ---- Encoders (requests; and responses, for a mock gateway) --------------------------------

    public static byte[] ExternalAddressRequest() => [Version, OpExternalAddress];

    public static byte[] MapRequest(bool udp, int internalPort, int suggestedExternalPort, TimeSpan lifetime)
    {
        var msg = new byte[12];
        msg[1] = udp ? OpMapUdp : OpMapTcp;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4), (ushort)internalPort);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(6), (ushort)suggestedExternalPort);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(8), (uint)lifetime.TotalSeconds);
        return msg;
    }

    public static byte[] ExternalAddressResponse(IPAddress external, ushort result = 0)
    {
        var msg = new byte[12];
        msg[1] = OpExternalAddress | ResponseFlag;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), result);
        external.GetAddressBytes().CopyTo(msg.AsSpan(8));
        return msg;
    }

    public static byte[] MapResponse(bool udp, int internalPort, int externalPort, TimeSpan lifetime, ushort result = 0)
    {
        var msg = new byte[16];
        msg[1] = (byte)((udp ? OpMapUdp : OpMapTcp) | ResponseFlag);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), result);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(8), (ushort)internalPort);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(10), (ushort)externalPort);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(12), (uint)lifetime.TotalSeconds);
        return msg;
    }

    // ---- Parsers -------------------------------------------------------------------------------

    public static IPAddress? ParseExternalAddress(ReadOnlySpan<byte> response)
    {
        if (response.Length < 12 || response[0] != Version || response[1] != (OpExternalAddress | ResponseFlag))
            return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(response[2..]) != 0)
            return null;
        return new IPAddress(response.Slice(8, 4).ToArray());
    }

    public static PortMapping? ParseMapResponse(ReadOnlySpan<byte> response, bool udp)
    {
        var expectedOp = (byte)((udp ? OpMapUdp : OpMapTcp) | ResponseFlag);
        if (response.Length < 16 || response[0] != Version || response[1] != expectedOp)
            return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(response[2..]) != 0)
            return null;
        var internalPort = BinaryPrimitives.ReadUInt16BigEndian(response[8..]);
        var externalPort = BinaryPrimitives.ReadUInt16BigEndian(response[10..]);
        var lifetime = BinaryPrimitives.ReadUInt32BigEndian(response[12..]);
        return new PortMapping(internalPort, externalPort, TimeSpan.FromSeconds(lifetime));
    }

    // ---- Network round-trips -------------------------------------------------------------------

    public static async Task<IPAddress?> QueryExternalAddressAsync(IPEndPoint gateway, TimeSpan timeout, CancellationToken cancellationToken = default)
        => ParseExternalAddress(await RoundtripAsync(gateway, ExternalAddressRequest(), timeout, cancellationToken).ConfigureAwait(false) ?? []);

    public static async Task<PortMapping?> MapAsync(IPEndPoint gateway, bool udp, int internalPort, int suggestedExternalPort, TimeSpan lifetime, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var response = await RoundtripAsync(gateway, MapRequest(udp, internalPort, suggestedExternalPort, lifetime), timeout, cancellationToken).ConfigureAwait(false);
        return response is null ? null : ParseMapResponse(response, udp);
    }

    private static async Task<byte[]?> RoundtripAsync(IPEndPoint gateway, byte[] request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(gateway);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await udp.SendAsync(request, cts.Token).ConfigureAwait(false);
            return (await udp.ReceiveAsync(cts.Token).ConfigureAwait(false)).Buffer;
        }
        catch { return null; }
    }

    /// <summary>The default IPv4 gateway from the up network interfaces, or null if none is found.</summary>
    public static IPAddress? DefaultGateway()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            foreach (var gateway in ni.GetIPProperties().GatewayAddresses)
                if (gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any))
                    return gateway.Address;
        }
        return null;
    }
}

/// <summary>
/// Requests a NAT-PMP mapping for the node's TCP listen port and returns it as a Mapped beacon — the automatic
/// "make me reachable" path. Best-effort: returns null when there is no gateway or it doesn't support NAT-PMP.
/// </summary>
public static class PortMapper
{
    public static async Task<Beacon?> TryMapTcpAsync(
        int internalPort, TimeSpan lifetime, TimeSpan timeout, CancellationToken cancellationToken = default, IPEndPoint? gateway = null)
    {
        gateway ??= NatPmp.DefaultGateway() is { } address ? new IPEndPoint(address, NatPmp.GatewayPort) : null;
        if (gateway is null)
            return null;

        var mapping = await NatPmp.MapAsync(gateway, udp: false, internalPort, internalPort, lifetime, timeout, cancellationToken).ConfigureAwait(false);
        if (mapping is null)
            return null;
        var external = await NatPmp.QueryExternalAddressAsync(gateway, timeout, cancellationToken).ConfigureAwait(false);
        if (external is null)
            return null;

        return new Beacon(EndpointKind.Mapped, external.ToString(), mapping.ExternalPort);
    }
}
