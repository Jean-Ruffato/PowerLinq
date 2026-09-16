using System.Linq.Expressions;

namespace PowerLinq.DaxConverter.Translators;

/// <summary>Whether the expression references the result lambda's parameter.</summary>
internal sealed class GroupReferenceFinder : ExpressionVisitor
{
    public bool Found { get; private set; }

    protected override Expression VisitParameter(ParameterExpression node)
    {
        Found = true;
        return node;
    }
}
