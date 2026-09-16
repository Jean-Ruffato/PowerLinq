namespace PowerLinq.DaxConverter.Translators;

/// <summary>
/// A resolved navigation path: the destination column and the hops that lead to it.
/// </summary>
/// <param name="Column">The DAX reference of the destination column, already qualified by its table.</param>
/// <param name="Hops">
/// The (source, destination) pairs of each hop, in order. One hop for a direct navigation; more for
/// <c>r.Exporter.Group.Name</c>.
/// </param>
/// <remarks>
/// The hops are kept because the check needs them: <c>RELATED</c> only holds if <b>every</b> hop is
/// many-to-one, and the refusal message has to say <i>which</i> hop broke. The generated DAX does
/// not use them — <c>RELATED</c> resolves the chain through the model.
/// </remarks>
internal sealed record DaxNavigationPath(
    string Column,
    IReadOnlyList<(string From, string To)> Hops);
