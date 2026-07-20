using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Arcanum;
using CupriNet.Codex;
using CupriNet.Concordance;
using CupriNet.Core;
using CupriNet.Rites;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Phase 5 hardening: every decoder must survive arbitrary/malformed input with only controlled
/// exceptions (never IndexOutOfRange / Overflow / OOM), and the overlay's Wards must resist floods.
/// </summary>
public class HardeningTests
{
    private static readonly Concordium Network = new("example.chat");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    [Fact]
    public void AllDecoders_OnArbitraryInput_ThrowOnlyControlledExceptions()
    {
        (string Name, Action<byte[]> Decode)[] decoders =
        [
            ("Codex", b => { var r = new CodexReader(b); while (!r.End) { r.ReadBytes(); } }),
            ("Intonation", b => IntonationCodec.Decode(b)),
            ("PeerRecord", b => PeerRecordCodec.Decode(b)),
            ("Decree", b => DecreeCodec.Decode(b)),
            ("Relationship", b => RelationshipCodec.Decode(b)),
            ("Epistle", b => EpistleCodec.Decode(b)),
            ("Conduit", b => ConduitCodec.Decode(b)),
            ("Reliquary", b => ReliquaryCodec.Decode(b)),
            ("Seal", b => SealCodec.Decode(b)),
        ];

        var rng = new Random(20260720);
        foreach (var (name, decode) in decoders)
        {
            for (var i = 0; i < 2000; i++)
            {
                var buffer = new byte[rng.Next(0, 300)];
                rng.NextBytes(buffer);
                try
                {
                    decode(buffer);
                }
                catch (CodexFormatException)
                {
                    // controlled — the only acceptable failure for a malformed document
                }
                catch (Exception ex)
                {
                    Assert.Fail($"{name} decoder threw {ex.GetType().Name} on arbitrary input: {ex.Message}");
                }
            }
        }
    }

    [Fact]
    public void IntonationValidator_NeverThrows_OnArbitraryInput()
    {
        var suite = CryptoSuites.Secure();
        var rng = new Random(4321);
        for (var i = 0; i < 2000; i++)
        {
            var buffer = new byte[rng.Next(0, 300)];
            rng.NextBytes(buffer);
            var result = IntonationValidator.ValidateDocument(buffer, Network, suite, Now);
            Assert.False(result.IsValid); // random noise is never a valid Intonation
        }
    }

    [Fact]
    public void Reliquary_Decode_RejectsHugeChunkCount_WithoutAllocating()
    {
        var w = new CodexWriter();
        w.WriteBytes(new byte[16]);      // transfer id
        w.WriteVarUInt(1);               // one file
        w.WriteString("a.bin");
        w.WriteUInt64(10);               // length
        w.WriteVarUInt(256);             // chunk size
        w.WriteBytes(new byte[32]);      // full hash
        w.WriteVarUInt(int.MaxValue);    // absurd chunk count, no data follows

        Assert.Throws<CodexFormatException>(() => ReliquaryCodec.Decode(w.ToArray()));
    }

    [Fact]
    public void Intonation_Decode_RejectsWrongLengthLitanySigil()
    {
        var body = new CodexWriter();
        body.WriteByte(IntonationCodec.CurrentVersion);
        body.WriteString(Network.Value);
        body.WriteBytes(new byte[32]);   // inviter key
        body.WriteVarUInt(0);            // no beacons
        body.WriteVarUInt(1);            // one Litany entry...
        body.WriteBytes(new byte[5]);    // ...with an invalid Sigil length
        body.WriteUInt64(0);
        body.WriteUInt64(0);
        body.WriteBytes(new byte[16]);   // nonce
        body.WriteByte(0);               // no petition

        var doc = new CodexWriter();
        doc.WriteBytes(body.ToArray());
        doc.WriteBytes(new byte[64]);    // signature slot

        Assert.Throws<CodexFormatException>(() => IntonationCodec.Decode(doc.ToArray()));
    }

    [Fact]
    public void Relationship_Decode_RejectsWrongLengthSigil()
    {
        var w = new CodexWriter();
        w.WriteBytes(new byte[5]);       // invalid Sigil length
        w.WriteBytes(new byte[32]);
        w.WriteBytes(new byte[32]);
        w.WriteBytes(new byte[32]);
        w.WriteBytes(new byte[64]);
        w.WriteUInt64(0);

        Assert.Throws<CodexFormatException>(() => RelationshipCodec.Decode(w.ToArray()));
    }

    [Fact]
    public void Constellation_HonestPeers_SurviveStrangerFlood()
    {
        var suite = CryptoSuites.Simulacrum();
        var constellation = new Constellation(new ConstellationOptions { MaxRecords = 100, MaxPerSlash24 = 10_000 });

        // Honest peers: 5 Kindred + 10 Wayfarers, each in a distinct /24.
        var honest = new List<Sigil>();
        for (var i = 0; i < 15; i++)
        {
            var record = Record(suite, $"10.{i}.0.1");
            constellation.Admit(record, i < 5 ? PeerBucket.Kindred : PeerBucket.Wayfarers, Now);
            honest.Add(record.Sigil);
        }

        // An attacker floods the table with hundreds of Stranger records.
        for (var j = 0; j < 500; j++)
            constellation.Admit(Record(suite, $"11.{j / 256}.{j % 256}.1"), PeerBucket.Strangers, Now);

        // Capacity Ward held, and no honest Kindred/Wayfarer was evicted by the flood.
        Assert.True(constellation.Count <= 100);
        foreach (var sigil in honest)
            Assert.NotNull(constellation.Get(sigil));
    }

    private static PeerRecord Record(ICryptoSuite suite, string ip)
    {
        var identity = NodeIdentity.Generate(suite);
        return PeerRecordSigner.Create(identity, [new Beacon(EndpointKind.Host, ip, 1)], 1, PeerCapabilities.None, suite, Now);
    }
}
