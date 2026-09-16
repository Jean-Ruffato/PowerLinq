namespace PowerLinq.DaxConverter.Model;

/// <summary>A divergence that was found.</summary>
/// <param name="Kind">The kind of divergence.</param>
/// <param name="Where">Where it is, in the code — type and member, when applicable.</param>
/// <param name="Message">What diverges, naming both sides.</param>
public sealed record DaxDivergence(DaxDivergenceKind Kind, string Where, string Message)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Kind} at {Where}: {Message}";
}
