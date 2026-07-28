using System.Net;
using CupriNet.Traversal;
using Xunit;

namespace CupriNet.UnitTests;

public class AddressPolicyTests
{
    [Theory]
    [InlineData("10.0.0.0/8", "10.5.6.7", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("192.168.0.0/16", "192.168.5.5", true)]
    [InlineData("192.168.0.0/16", "192.169.0.1", false)]
    [InlineData("192.168.1.0/24", "192.168.1.200", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("10.0.0.0/255.0.0.0", "10.1.2.3", true)]      // dotted-netmask form
    [InlineData("203.0.113.7", "203.0.113.7", true)]          // bare IP = host route
    [InlineData("203.0.113.7", "203.0.113.8", false)]
    [InlineData("2001:db8::/32", "2001:db8:1234::1", true)]   // IPv6
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]                // match-all v4
    public void SubnetRange_Contains(string cidr, string ip, bool expected)
    {
        Assert.True(SubnetRange.TryParse(cidr, out var range));
        Assert.Equal(expected, range!.Contains(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("10.0.0.0/33")]     // prefix out of range
    [InlineData("not-an-ip/24")]
    [InlineData("10.0.0.0/255.0.255.0")] // non-contiguous netmask
    public void SubnetRange_RejectsInvalid(string bad)
        => Assert.False(SubnetRange.TryParse(bad, out _));

    [Fact]
    public void Empty_AllowsEverything()
    {
        var policy = AddressPolicy.Parse(null, null);
        Assert.True(policy.IsEmpty);
        Assert.True(policy.IsAllowed(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void Deny_BlocksMatch_ButDefaultsAllow()
    {
        var policy = AddressPolicy.Parse(null, ["10.0.0.0/8"]);
        Assert.False(policy.IsAllowed(IPAddress.Parse("10.1.1.1")));
        Assert.True(policy.IsAllowed(IPAddress.Parse("8.8.8.8")));  // not denied -> allowed
    }

    [Fact]
    public void Allow_AlwaysBeatsDeny()
    {
        var policy = AddressPolicy.Parse(["10.5.0.0/16"], ["10.0.0.0/8"]);
        Assert.True(policy.IsAllowed(IPAddress.Parse("10.5.1.1")));   // whitelisted despite the blacklist
        Assert.False(policy.IsAllowed(IPAddress.Parse("10.6.1.1")));  // still blacklisted
    }

    [Fact]
    public void PrivateNetwork_IsDenyAll_PlusAllowSubnet()
    {
        // The canonical "private CupriNet / LAN-only" shape.
        var policy = AddressPolicy.Parse(["192.168.0.0/16"], ["0.0.0.0/0"]);
        Assert.True(policy.IsAllowed(IPAddress.Parse("192.168.1.1")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("8.8.8.8")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void MisconfiguredSubnet_ThrowsLoudly()
    {
        Assert.Throws<FormatException>(() => AddressPolicy.Parse(null, ["not-a-subnet"]));
        Assert.Throws<FormatException>(() => AddressPolicy.Parse(["10.0.0.0/999"], null));
    }
}
