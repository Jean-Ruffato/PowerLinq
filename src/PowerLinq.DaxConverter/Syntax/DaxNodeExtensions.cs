namespace PowerLinq.DaxConverter.Syntax;

/// <summary>Writing shortcuts over the tree.</summary>
public static class DaxNodeExtensions
{
    /// <summary>Renders the node as DAX text.</summary>
    public static string ToDaxString(this IDaxNode node) => DaxWriter.Render(node);
}
