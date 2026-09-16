using PowerLinq.DaxConverter.Interfaces;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>A compiled query with one parameter.</summary>
/// <typeparam name="T">The table's entity.</typeparam>
/// <typeparam name="TParam">The type of the parameter's value.</typeparam>
public sealed class DaxCompiledQuery<T, TParam>(
    Func<IDaxTable<T>, DaxQuery<T>> build,
    int parameterCount) : DaxCompiledQueryBase<T>(build, parameterCount) where T : class
{
    /// <summary>The DAX with the value bound.</summary>
    /// <param name="table">The table.</param>
    /// <param name="value">The parameter's value.</param>
    public string ToDaxString(IDaxTable<T> table, TParam value) => Bind(table, [value]);
}
