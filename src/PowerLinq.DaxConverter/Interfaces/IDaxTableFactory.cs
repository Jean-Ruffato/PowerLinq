namespace PowerLinq.DaxConverter.Interfaces;

/// <summary>Abstracts table creation so the context does not depend on the concrete implementation.</summary>
public interface IDaxTableFactory
{
    /// <summary>Creates the table of the given type, bound to the executor.</summary>
    IDaxTable<T> Create<T>(IDaxQueryExecutor executor) where T : class;
}
