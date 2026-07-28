using System.Net;
using CupriNet.Traversal;
using Xunit;

namespace CupriNet.UnitTests;

public class NetworkReachabilityTests
{
    [Theory]
    // Public IPv4 (the TEST-NET doc ranges are not RFC1918, so treated as routable stand-ins for public).
    [InlineData("8.8.8.8", true)]
    [InlineData("203.0.113.7", true)]
    [InlineData("198.51.100.9", true)]
    // Non-routable IPv4.
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.5.5", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]   // CGNAT
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]  // link-local
    [InlineData("224.0.0.1", false)]    // multicast
    [InlineData("0.0.0.0", false)]
    // IPv6.
    [InlineData("2606:4700:4700::1111", true)] // global unicast
    [InlineData("::1", false)]                  // loopback
    [InlineData("fe80::1", false)]              // link-local
    [InlineData("fc00::1", false)]              // unique-local
    [InlineData("2001:db8::1", false)]          // documentation
    [InlineData("2002:c0a8:0101::1", false)]    // 6to4
    [InlineData("2001::1", false)]              // Teredo 2001:0000::/32
    public void IsPubliclyRoutable_ClassifiesAddresses(string ip, bool expected)
        => Assert.Equal(expected, NetworkReachability.IsPubliclyRoutable(IPAddress.Parse(ip)));
}
