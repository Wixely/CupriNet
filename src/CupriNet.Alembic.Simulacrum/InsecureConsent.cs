namespace CupriNet.Alembic.Simulacrum;

/// <summary>
/// A capability token that must be presented to construct the insecure <see cref="SimulacrumSuite"/>.
/// Its only purpose is to make choosing no-real-cryptography an explicit, greppable, deliberate act.
/// </summary>
public sealed class InsecureConsent
{
    private InsecureConsent()
    {
    }

    /// <summary>
    /// Acknowledges that the Simulacrum provides NO confidentiality, integrity, or authenticity and is
    /// for development and testing only. Never call this on a path reachable in a production build.
    /// </summary>
    public static InsecureConsent IUnderstandThisProvidesNoSecurity() => new();
}
