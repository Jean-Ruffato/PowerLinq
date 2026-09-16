namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// What a <see cref="DaxParameter{T}"/> exposes to the translator, without the value's type.
/// </summary>
/// <remarks>
/// It exists so the translator can find the slot without knowing <c>T</c>: it locates the marker by
/// reflection over a closed expression, and there the generic type is not available statically.
/// </remarks>
public interface IDaxParameter
{
    /// <summary>The parameter's position in the list of values, zero-based.</summary>
    int Slot { get; }
}
