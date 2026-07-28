using System.Net;
using System.Net.Sockets;
using CupriNet.Core;
using CupriNet.Traversal;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// NAT-PMP auto port-mapping: the wire codec, and a full request/response round-trip against an in-process mock
/// gateway (real routers are untestable in CI). Proves the mechanism that turns a cooperating router into a
/// directly-reachable Mapped beacon with no manual port-forwarding.
/// </summary>
public class NatPmpTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void MapResponse_RoundTrips()
    {
        var wire = NatPmp.MapResponse(udp: false, internalPort: 43820, externalPort: 51000, lifetime: TimeSpan.FromMinutes(30));
        var mapping = NatPmp.ParseMapResponse(wire, udp: false);
        Assert.NotNull(mapping);
        Assert.Equal(43820, mapping!.InternalPort);
        Assert.Equal(51000, mapping.ExternalPort);
        Assert.Equal(TimeSpan.FromMinutes(30), mapping.Lifetime);

        Assert.Null(NatPmp.ParseMapResponse(wire, udp: true));                 // wrong protocol op rejected
        Assert.Null(NatPmp.ParseMapResponse(NatPmp.MapResponse(false, 1, 2, TimeSpan.Zero, result: 3), udp: false)); // error result rejected
    }

    [Fact]
    public void ExternalAddress_RoundTrips()
    {
        var wire = NatPmp.ExternalAddressResponse(IPAddress.Parse("203.0.113.7"));
        Assert.Equal(IPAddress.Parse("203.0.113.7"), NatPmp.ParseExternalAddress(wire));
    }

    [Fact]
    public async Task PortMapper_AgainstMockGateway_ProducesAMappedBeacon()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        // Mock gateway: answer external-address with 198.51.100.9, and map requests with external = internal + 5000.
        using var gateway = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var gatewayEp = (IPEndPoint)gateway.Client.LocalEndPoint!;
        var pump = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult req;
                try { req = await gateway.ReceiveAsync(ct); } catch { break; }
                var buf = req.Buffer;
                byte[] resp;
                if (buf[1] == 0) // external-address request
                    resp = NatPmp.ExternalAddressResponse(IPAddress.Parse("198.51.100.9"));
                else // map request: internalPort at [4..6]
                {
                    var internalPort = (buf[4] << 8) | buf[5];
                    resp = NatPmp.MapResponse(udp: false, internalPort, internalPort + 5000, TimeSpan.FromMinutes(30));
                }
                await gateway.SendAsync(resp, req.RemoteEndPoint, ct);
            }
        }, ct);

        var beacon = await PortMapper.TryMapTcpAsync(43820, TimeSpan.FromMinutes(30), Timeout, ct, gatewayEp);

        Assert.NotNull(beacon);
        Assert.Equal(EndpointKind.Mapped, beacon!.Kind);
        Assert.Equal("198.51.100.9", beacon.Host);
        Assert.Equal(48820, beacon.Port); // 43820 + 5000, as the mock mapped it

        await cts.CancelAsync();
        try { await pump; } catch { }
    }

    [Fact]
    public async Task PortMapper_RejectsNonPublicExternalAddress()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        // A malicious/misconfigured gateway claims a private external address — it must not become a Mapped beacon.
        using var gateway = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var gatewayEp = (IPEndPoint)gateway.Client.LocalEndPoint!;
        var pump = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult req;
                try { req = await gateway.ReceiveAsync(ct); } catch { break; }
                var buf = req.Buffer;
                byte[] resp;
                if (buf[1] == 0)
                    resp = NatPmp.ExternalAddressResponse(IPAddress.Parse("192.168.1.50")); // private — bogus
                else
                {
                    var internalPort = (buf[4] << 8) | buf[5];
                    resp = NatPmp.MapResponse(udp: false, internalPort, internalPort + 5000, TimeSpan.FromMinutes(30));
                }
                await gateway.SendAsync(resp, req.RemoteEndPoint, ct);
            }
        }, ct);

        var beacon = await PortMapper.TryMapTcpAsync(43820, TimeSpan.FromMinutes(30), Timeout, ct, gatewayEp);
        Assert.Null(beacon); // a private "external" address is refused, not advertised

        await cts.CancelAsync();
        try { await pump; } catch { }
    }
}
