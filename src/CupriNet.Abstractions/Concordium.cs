namespace CupriNet.Abstractions;

/// <summary>
/// Identifies which network (Concordance) a document belongs to. Documents carrying a
/// different Concordium are rejected before any further processing.
/// </summary>
public readonly record struct Concordium(string Value)
{
    public override string ToString() => Value;
}
