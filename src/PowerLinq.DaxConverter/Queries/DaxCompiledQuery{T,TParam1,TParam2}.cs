using PowerLinq.DaxConverter.Interfaces;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>A compiled query with two parameters.</summary>
/// <typeparam name="T">The table's entity.</typeparam>
/// <typeparam name="TParam1">The type of the first parameter's value.</typeparam>
/// <typeparam name="TParam2">The type of the second parameter's value.</typeparam>
public sealed class DaxCompiledQuery<T, TParam1, TParam2>(
    Func<IDaxTable<T>, DaxQuery<T>> build,
    int parameterCount) : DaxCompiledQueryBase<T>(build, parameterCount) where T : class
{
    /// <summary>The DAX with the values bound.</summary>
    /// <param name="table">The table.</param>
    /// <param name="first">The first parameter's value.</param>
    /// <param name="second">The second parameter's value.</param>
    public string ToDaxString(IDaxTable<T> table, TParam1 first, TParam2 second) =>
        Bind(table, [first, second]);
}
