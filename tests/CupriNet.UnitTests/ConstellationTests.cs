using CupriNet.Alembic;
using CupriNet.Concordance;
using CupriNet.Core;
using Xunit;

namespace CupriNet.UnitTests;

public class ConstellationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static PeerRecord Record(ICryptoSuite suite, string ip, ulong seq = 1)
    {
        var identity = NodeIdentity.Generate(suite);
        return PeerRecordSigner.Create(identity, [new Beacon(EndpointKind.Host, ip, 43820)], seq, PeerCapabilities.None, suite, Now);
    }

    [Fact]
    public void Admit_NewPeer_ThenUpdateOnlyOnNewerSequence()
    {
        var suite = CryptoSuites.Simulacrum();
        var c = new Constellation();
        var identity = NodeIdentity.Generate(suite);

        var v1 = PeerRecordSigner.Create(identity, [new Beacon(EndpointKind.Host, "10.0.0.5", 1)], 1, PeerCapabilities.None, suite, Now);
        var v2 = PeerRecordSigner.Create(identity, [new Beacon(EndpointKind.Host, "10.0.0.5", 1)], 2, PeerCapabilities.None, suite, Now);

        Assert.Equal(AdmissionResult.Admitted, c.Admit(v1, PeerBucket.Strangers, Now));
        Assert.Equal(AdmissionResult.RejectedStale, c.Admit(v1, PeerBucket.Strangers, Now)); // equal seq
        Assert.Equal(AdmissionResult.Updated, c.Admit(v2, PeerBucket.Strangers, Now));       // newer seq
        Assert.Equal(1, c.Count);
        Assert.Equal(2UL, c.Get(identity.Sigil)!.Record.SequenceNumber);
    }

    [Fact]
    public void DiversityQuota_LimitsPeersPerSlash24()
    {
        var suite = CryptoSuites.Simulacrum();
        var c = new Constellation(new ConstellationOptions { MaxPerSlash24 = 2 });

        Assert.Equal(AdmissionResult.Admitted, c.Admit(Record(suite, "10.0.0.5"), PeerBucket.Strangers, Now));
        Assert.Equal(AdmissionResult.Admitted, c.Admit(Record(suite, "10.0.0.6"), PeerBucket.Strangers, Now));
        // Third peer in the same /24 is refused...
        Assert.Equal(AdmissionResult.RejectedDiversity, c.Admit(Record(suite, "10.0.0.7"), PeerBucket.Strangers, Now));
        // ...but a different /24 is fine.
        Assert.Equal(AdmissionResult.Admitted, c.Admit(Record(suite, "10.0.1.5"), PeerBucket.Strangers, Now));
    }

    [Fact]
    public void SizeWard_EvictsStranger_WhenFull()
    {
        var suite = CryptoSuites.Simulacrum();
        var c = new Constellation(new ConstellationOptions { MaxRecords = 2, MaxPerSlash24 = 10 });

        c.Admit(Record(suite, "10.0.0.5"), PeerBucket.Strangers, Now);
        c.Admit(Record(suite, "10.0.1.5"), PeerBucket.Strangers, Now);
        Assert.Equal(2, c.Count);

        // Table is full; admitting a new peer evicts an expendable Stranger rather than rejecting.
        Assert.Equal(AdmissionResult.Admitted, c.Admit(Record(suite, "10.0.2.5"), PeerBucket.Strangers, Now));
        Assert.Equal(2, c.Count);
    }

    [Fact]
    public void SizeWard_RejectsFull_WhenNothingExpendable()
    {
        var suite = CryptoSuites.Simulacrum();
        var c = new Constellation(new ConstellationOptions { MaxRecords = 1, MaxPerSlash24 = 10 });

        var kindred = Record(suite, "10.0.0.5");
        c.Admit(kindred, PeerBucket.Kindred, Now); // Kindred is not expendable
        Assert.Equal(AdmissionResult.RejectedFull, c.Admit(Record(suite, "10.0.1.5"), PeerBucket.Strangers, Now));
    }

    [Fact]
    public void Taint_PastThreshold_Excommunicates_AndFreesDiversitySlot()
    {
        var suite = CryptoSuites.Simulacrum();
        var c = new Constellation(new ConstellationOptions { MaxPerSlash24 = 1, TaintQuarantineThreshold = 2 });

        var bad = Record(suite, "10.0.0.5");
        Assert.Equal(AdmissionResult.Admitted, c.Admit(bad, PeerBucket.Strangers, Now));
        // Same /24 blocked while the first peer is healthy.
        Assert.Equal(AdmissionResult.RejectedDiversity, c.Admit(Record(suite, "10.0.0.6"), PeerBucket.Strangers, Now));

        c.Taint(bad.Sigil, 2); // quarantine
        Assert.Equal(PeerBucket.Excommunicate, c.Get(bad.Sigil)!.Bucket);

        // Excommunicated peers do not count toward the /24 quota.
        Assert.Equal(AdmissionResult.Admitted, c.Admit(Record(suite, "10.0.0.6"), PeerBucket.Strangers, Now));
    }

    [Fact]
    public void Sample_IsBounded_DiversePreferred_AndHidesExcommunicated()
    {
        var suite = CryptoSuites.Simulacrum();
        var c = new Constellation(new ConstellationOptions { MaxPerSlash24 = 10 });

        var a = Record(suite, "10.0.0.5");
        var b = Record(suite, "10.0.1.5");
        var evil = Record(suite, "10.0.2.5");
        c.Admit(a, PeerBucket.Wayfarers, Now);
        c.Admit(b, PeerBucket.Wayfarers, Now);
        c.Admit(evil, PeerBucket.Strangers, Now);
        c.Taint(evil.Sigil, 5); // excommunicate

        var sample = c.Sample(10);
        Assert.Equal(2, sample.Count); // evil is hidden
        Assert.DoesNotContain(sample, r => r.Sigil == evil.Sigil);

        Assert.True(c.Sample(1).Count <= 1); // respects the cap
    }
}
