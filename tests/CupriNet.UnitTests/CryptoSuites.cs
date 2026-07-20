using CupriNet.Alembic;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Alembic.Simulacrum;

namespace CupriNet.UnitTests;

/// <summary>Shared crypto-suite fixtures for parity and security tests.</summary>
public static class CryptoSuites
{
    public static SimulacrumSuite Simulacrum()
        => new(InsecureConsent.IUnderstandThisProvidesNoSecurity());

    public static BouncyCastleSuite Secure() => new();

    /// <summary>Both suites, for parity theories that must pass identically on each.</summary>
    public static IEnumerable<object[]> All()
    {
        yield return [(ICryptoSuite)Simulacrum()];
        yield return [(ICryptoSuite)Secure()];
    }
}
