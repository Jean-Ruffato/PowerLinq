using System.Reflection;
using PowerLinq.ConnectionPool.Executors;
using PowerLinq.DaxConverter.Mapping;

namespace PowerLinq.Tests.Mapping;

/// <summary>
/// The entity has to be a reference type.
/// </summary>
/// <remarks>
/// <para>
/// <c>where T : new()</c> accepts a <c>struct</c>, and materialization then returned <b>everything
/// at its default value</b>: the entity is boxed at the assignment boundary, the write happens on
/// the box and the box is discarded on return. A silent and plausible failure — the DAX is right,
/// the row count is right, and every value is zero, which is a possible total on a dashboard.
/// </para>
/// <para>
/// The case no longer compiles, so there is no way to test it at run time. What can be tested is
/// the <b>constraint</b>, by reflection — and it is the constraint that needs a guard, because
/// removing it breaks nothing visible: it compiles again and returns zero again.
/// </para>
/// <para>
/// Almost nothing in the library requires <c>new()</c> any more: materialization came to accept a
/// parameterized constructor, and the constraint became <c>class</c> on its own. The scan still
/// holds — and holds <b>more</b>: if anyone reintroduces <c>new()</c> on any parameter, it demands
/// that <c>class</c> comes along. What it guards is the combination, not the presence.
/// </para>
/// </remarks>
public sealed class EntityConstraintTests
{
    /// <summary>
    /// It holds for the whole surface, not for a hand-written list of types: a new type with
    /// <c>new()</c> and without <c>class</c> fails here without anyone having to remember to add it.
    /// </summary>
    [Fact]
    public void EveryTypeParameterRequiringAConstructor_AlsoRequiresAReferenceType()
    {
        Assembly[] assemblies =
        [
            typeof(EntityMapper).Assembly,
            typeof(PooledXmlaQueryExecutor).Assembly
        ];

        List<string> offenders = [.. assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(TypeParameters)
            .Where(entry => RequiresConstructor(entry.Parameter) && !RequiresReferenceType(entry.Parameter))
            .Select(entry => entry.Description)];

        Assert.Empty(offenders);
    }

    /// <summary>Proves the test above sees what it should: a parameter without the constraint.</summary>
    [Fact]
    public void TheScan_DetectsAMissingReferenceTypeConstraint()
    {
        (Type Parameter, string Description)[] parameters = [.. TypeParameters(typeof(SemRestricao<>))];

        Assert.Single(parameters);
        Assert.True(RequiresConstructor(parameters[0].Parameter));
        Assert.False(RequiresReferenceType(parameters[0].Parameter));
    }

    [Fact]
    public void TheScan_AcceptsAConstrainedParameter()
    {
        (Type Parameter, string Description)[] parameters = [.. TypeParameters(typeof(ComRestricao<>))];

        Assert.Single(parameters);
        Assert.True(RequiresConstructor(parameters[0].Parameter));
        Assert.True(RequiresReferenceType(parameters[0].Parameter));
    }

    private sealed class SemRestricao<T> where T : new();

    private sealed class ComRestricao<T> where T : class, new();

    private static IEnumerable<(Type Parameter, string Description)> TypeParameters(Type type)
    {
        if (type.IsGenericTypeDefinition)
        {
            foreach (Type parameter in type.GetGenericArguments())
                yield return (parameter, $"{type.Name}<{parameter.Name}>");
        }

        MethodInfo[] methods = type.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        foreach (MethodInfo method in methods.Where(candidate => candidate.IsGenericMethodDefinition))
        {
            foreach (Type parameter in method.GetGenericArguments())
                yield return (parameter, $"{type.Name}.{method.Name}<{parameter.Name}>");
        }
    }

    private static bool RequiresConstructor(Type parameter) =>
        parameter.GenericParameterAttributes
            .HasFlag(GenericParameterAttributes.DefaultConstructorConstraint);

    private static bool RequiresReferenceType(Type parameter) =>
        parameter.GenericParameterAttributes
            .HasFlag(GenericParameterAttributes.ReferenceTypeConstraint);
}
