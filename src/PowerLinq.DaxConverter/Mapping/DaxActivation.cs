using System.Collections.Frozen;
using System.Linq.Expressions;
using System.Reflection;
using PowerLinq.DaxConverter.Attributes;

namespace PowerLinq.DaxConverter.Mapping;

/// <summary>
/// How to create an instance of the result type: through a parameterless constructor plus property
/// assignment, or through a constructor with parameters.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule is that the parameterless constructor wins.</b> If the type has one, materialization
/// uses writable properties — the path that has always existed, so no current contract changes
/// behaviour. Only in its absence does the parameterized constructor come into play.
/// </para>
/// <para>
/// The rule resolves the ambiguity without heuristics: a <c>record</c> with <c>{ get; init; }</c>
/// and an empty constructor takes the old path — <c>init</c> is a setter in IL — and a positional
/// <c>record</c>, an anonymous type or an immutable DTO, which have no empty constructor, take the
/// new one. The alternative would be picking whichever "fits best", and then the same class could
/// switch paths just by gaining a column in the result.
/// </para>
/// <para>
/// With <b>more than one</b> parameterized constructor, the highest arity wins. A tie is refused:
/// silently choosing between two constructors of the same arity would mean materialization depends
/// on the order reflection returns them in.
/// </para>
/// </remarks>
internal sealed class DaxActivation
{
    /// <summary>Compiled factory for the parameterless constructor, when the type has one.</summary>
    /// <remarks>
    /// Compiled, not <c>Activator.CreateInstance</c>: it is the same reason as the compiled setters
    /// of <see cref="DaxColumnMapping"/> — creation happens once per row, and <c>Activator</c> pays
    /// for a type check on every call.
    /// </remarks>
    public Func<object>? Create { get; }

    /// <summary>The parameterized constructor, when there is no parameterless one.</summary>
    public ConstructorInfo? Constructor { get; }

    /// <summary>Column reference to the position of the parameter that receives it.</summary>
    public FrozenDictionary<string, int> ParameterByColumn { get; }

    /// <summary>The type of each parameter, for the conversion.</summary>
    public Type[] ParameterTypes { get; }

    /// <summary>
    /// The value of each parameter when the column is absent from the result — the type's
    /// <c>default</c>.
    /// </summary>
    /// <remarks>
    /// Precomputed because it is immutable and identical on every row: a missing <c>int</c> becomes
    /// 0 and a missing <c>string</c> becomes <see langword="null"/>, which is the same semantics as
    /// the property path — there the property simply is not assigned.
    /// </remarks>
    public object?[] Defaults { get; }

    private DaxActivation(
        Func<object>? create,
        ConstructorInfo? constructor,
        FrozenDictionary<string, int> parameterByColumn,
        Type[] parameterTypes,
        object?[] defaults)
    {
        Create = create;
        Constructor = constructor;
        ParameterByColumn = parameterByColumn;
        ParameterTypes = parameterTypes;
        Defaults = defaults;
    }

    /// <summary>Builds the plan for the type.</summary>
    /// <param name="type">The result type.</param>
    /// <param name="mappings">The type's column mapping, from <see cref="EntityMapper.GetColumnMappings"/>.</param>
    /// <exception cref="NotSupportedException">
    /// The type has no usable constructor, or has two of the same maximum arity.
    /// </exception>
    public static DaxActivation For(
        Type type,
        FrozenDictionary<string, DaxColumnMapping> mappings)
    {
        if (type.GetConstructor(Type.EmptyTypes) is not null)
        {
            return new DaxActivation(
                Expression.Lambda<Func<object>>(Expression.New(type)).Compile(),
                constructor: null,
                FrozenDictionary<string, int>.Empty,
                [],
                []);
        }

        ConstructorInfo constructor = Choose(type);
        ParameterInfo[] parameters = constructor.GetParameters();

        var byColumn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < parameters.Length; i++)
        {
            foreach (string reference in ColumnsOf(parameters[i], mappings))
                byColumn[reference] = i;
        }

        return new DaxActivation(
            create: null,
            constructor,
            byColumn.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            [.. parameters.Select(parameter => parameter.ParameterType)],
            [.. parameters.Select(parameter => Default(parameter.ParameterType))]);
    }

    /// <summary>
    /// The column references that feed the parameter.
    /// </summary>
    /// <remarks>
    /// The property of the same name wins when it exists: in a positional <c>record</c> it is
    /// generated alongside and may carry <c>[DaxColumn]</c>, and ignoring it would make the same
    /// type map differently depending on which path materialized it. With no matching property, the
    /// parameter itself may carry the attribute; with neither, the name and the bracketed name
    /// apply.
    /// </remarks>
    private static IEnumerable<string> ColumnsOf(
        ParameterInfo parameter,
        FrozenDictionary<string, DaxColumnMapping> mappings)
    {
        string name = parameter.Name ?? string.Empty;

        IEnumerable<string> fromProperty = mappings
            .Where(entry => string.Equals(entry.Value.Property.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key);

        if (fromProperty.Any())
            return fromProperty;

        if (parameter.GetCustomAttribute<DaxColumnAttribute>() is { } attribute)
            return [attribute.ColumnReference];

        return [name, $"[{name}]"];
    }

    private static ConstructorInfo Choose(Type type)
    {
        ConstructorInfo[] candidates = [.. type.GetConstructors()
            .Where(constructor => constructor.GetParameters().Length > 0)
            .OrderByDescending(constructor => constructor.GetParameters().Length)];

        if (candidates.Length == 0)
            throw new NotSupportedException(Message("MaterializationNoConstructor", type));

        int arity = candidates[0].GetParameters().Length;

        return candidates.Count(constructor => constructor.GetParameters().Length == arity) == 1
            ? candidates[0]
            : throw new NotSupportedException(Message("MaterializationAmbiguousConstructor", type));
    }

    /// <remarks>
    /// The message comes out in English: the plan is cached per type and shared across queries, so
    /// it cannot carry the language of any one of them. The case is a contract modelling error,
    /// which surfaces on the first execution and does not depend on data.
    /// </remarks>
    private static string Message(string key, Type type) =>
        Localization.ResourceManagerPowerLinqLocalizer.English.Format(key, type.Name);

    private static object? Default(Type type) =>
        type.IsValueType ? Activator.CreateInstance(type) : null;
}
