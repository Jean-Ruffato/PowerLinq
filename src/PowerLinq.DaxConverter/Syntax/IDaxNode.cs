namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// A node of the DAX syntax tree. Every node knows how to write itself.
/// </summary>
/// <remarks>
/// The write takes a <see cref="DaxWriter"/> instead of returning a string because an isolated
/// node does not know its own context: without knowing the parent's precedence it would have to
/// parenthesize always, and without knowing the depth it would have no way to indent. The writer
/// carries that context, and the node still decides its own syntax.
/// </remarks>
public interface IDaxNode
{
    /// <inheritdoc/>
    void Write(DaxWriter writer);
}
