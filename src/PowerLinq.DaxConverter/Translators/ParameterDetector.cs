using System.Linq.Expressions;

namespace PowerLinq.DaxConverter.Translators;

internal sealed class ParameterDetector : ExpressionVisitor
{
    private bool _found;

    public static bool Detect(Expression expression)
    {
        var detector = new ParameterDetector();
        detector.Visit(expression);

        return detector._found;
    }

    protected override Expression VisitParameter(ParameterExpression node)
    {
        _found = true;
        return node;
    }
}
