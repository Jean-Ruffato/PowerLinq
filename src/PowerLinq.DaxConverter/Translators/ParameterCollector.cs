using System.Linq.Expressions;

namespace PowerLinq.DaxConverter.Translators;

/// <summary>Collects the parameters the expression references.</summary>
internal sealed class ParameterCollector : ExpressionVisitor
{
    public HashSet<ParameterExpression> Parameters { get; } = [];

    protected override Expression VisitParameter(ParameterExpression node)
    {
        Parameters.Add(node);
        return base.VisitParameter(node);
    }
}
