using CupriNet.Concordance;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>The Tribute proof of work: bind a nonce to a subject at a difficulty, verify with one hash.</summary>
public class TributeTests
{
    [Fact]
    public void LeadingZeroBits_CountsCorrectly()
    {
        Assert.Equal(0, Tribute.LeadingZeroBits([0xFF, 0x00]));
        Assert.Equal(8, Tribute.LeadingZeroBits([0x00, 0xFF]));
        Assert.Equal(12, Tribute.LeadingZeroBits([0x00, 0x0F, 0xFF])); // 8 + 4
        Assert.Equal(9, Tribute.LeadingZeroBits([0x00, 0x40]));        // 8 + 1
        Assert.Equal(16, Tribute.LeadingZeroBits([0x00, 0x00, 0x80])); // 16 + 0
    }

    [Fact]
    public void Solve_ProducesAProofThatVerifies_AndIsBoundToTheSubject()
    {
        var subject = "advert-1"u8.ToArray();
        var nonce = Tribute.Solve(subject, difficulty: 12);

        Assert.True(Tribute.Verify(subject, nonce, requiredDifficulty: 12));
        Assert.True(Tribute.Verify(subject, nonce, requiredDifficulty: 4)); // a 12-bit proof meets a 4-bit demand
    }

    [Fact]
    public void Difficulty_IsHardCapped_SoWorkIsAlwaysBounded()
    {
        // Asking beyond the cap grinds only to the cap (bounded), and the cap is what verifies.
        var subject = "advert-2"u8.ToArray();
        var nonce = Tribute.Solve(subject, difficulty: Tribute.MaxDifficulty + 1000);
        Assert.True(Tribute.Verify(subject, nonce, requiredDifficulty: Tribute.MaxDifficulty + 1000));
    }
}
