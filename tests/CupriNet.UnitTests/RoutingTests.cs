using CupriNet.Concordance;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

public class RoutingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    [Fact]
    public void Ascendant_IsDeterministic_And32Bytes()
    {
        byte[] pub = [1, 2, 3, 4];
        var a = RoutingKey.FromSealPublicKey(pub);
        var b = RoutingKey.FromSealPublicKey(pub);
        Assert.Equal(RoutingKey.Size, a.Span.Length);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Distance_ToSelf_IsSmallerThan_ToOthers()
    {
        var a = RoutingKey.FromToken([1]);
        var b = RoutingKey.FromToken([2]);

        // The key closest to a target is the target itself (XOR distance 0).
        Assert.True(a.DistanceTo(a) < b.DistanceTo(a));
    }

    [Fact]
    public void ClosestTo_ReturnsTheNearestKnownPeer()
    {
        var suite = CryptoSuites.Simulacrum();
        var constellation = new Constellation(new ConstellationOptions { MaxPerSlash24 = 1000 });

        var records = new List<PeerRecord>();
        for (var i = 0; i < 6; i++)
        {
            var identity = NodeIdentity.Generate(suite);
            var record = PeerRecordSigner.Create(identity, [new Beacon(EndpointKind.Host, $"10.0.{i}.1", 1)], 1, PeerCapabilities.None, suite, Now);
            constellation.Admit(record, PeerBucket.Strangers, Now);
            records.Add(record);
        }

        var target = RoutingKey.FromSealPublicKey(records[3].SealPublicKey);
        var closest = constellation.ClosestTo(target, 1);

        Assert.Single(closest);
        Assert.Equal(records[3].Sigil, closest[0].Sigil); // exact match is distance 0
    }
}
