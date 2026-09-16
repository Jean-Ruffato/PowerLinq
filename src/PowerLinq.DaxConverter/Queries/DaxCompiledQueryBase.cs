using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// A compiled query's template, and the common base of the arities.
/// </summary>
/// <typeparam name="T">The table's entity.</typeparam>
/// <remarks>
/// The arity lives in the subclasses because it is what gives the values their type at the call
/// site — it is what makes passing an <c>int</c> where the query expects text a compile error, and
/// not invalid DAX discovered on the server.
/// </remarks>
public abstract class DaxCompiledQueryBase<T> where T : class
{
    private readonly Func<IDaxTable<T>, DaxQuery<T>> _build;
    private readonly int _parameterCount;

    /// <remarks>
    /// No <c>lock</c>. Two simultaneous executions on the first run may both build the template,
    /// and only one is kept — which is harmless because it is determined by <c>_build</c> and by
    /// <typeparamref name="T"/>, so the two are identical. A <c>lock</c> here would serialize every
    /// subsequent read to protect a race with no consequence.
    /// </remarks>
    private DaxTemplate? _template;

    internal DaxCompiledQueryBase(Func<IDaxTable<T>, DaxQuery<T>> build, int parameterCount)
    {
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _parameterCount = parameterCount;
    }

    /// <summary>
    /// The template, built on the first call and reused on the following ones.
    /// </summary>
    /// <param name="table">The table; only its entity affects the DAX, not the executor.</param>
    /// <exception cref="InvalidOperationException">
    /// The composition did not use every declared parameter. That is an error, not a detail: a
    /// declared and unused parameter makes the call ask for a value that goes nowhere, and whoever
    /// reads the call believes it filters.
    /// </exception>
    public DaxTemplate TemplateFor(IDaxTable<T> table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (_template is { } cached)
            return cached;

        DaxTemplate template = DaxWriter.RenderTemplate(_build(table).ToSyntaxTree());

        if (template.ParameterCount != _parameterCount)
        {
            throw new InvalidOperationException(
                $"This compiled query declares {_parameterCount} parameter(s), but the composition "
                + $"used {template.ParameterCount}. Every declared parameter has to reach the DAX, "
                + "otherwise the call asks for a value that goes nowhere.");
        }

        return _template = template;
    }

    /// <summary>Binds the values and returns the DAX.</summary>
    protected string Bind(IDaxTable<T> table, IReadOnlyList<object?> values) =>
        TemplateFor(table).Bind(values);
}
