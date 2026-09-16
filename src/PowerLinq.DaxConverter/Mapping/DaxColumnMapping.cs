using System.Linq.Expressions;
using System.Reflection;

namespace PowerLinq.DaxConverter.Mapping;

/// <summary>
/// The link between a result column and the property that receives it, with everything
/// materialization needs already resolved.
/// </summary>
/// <remarks>
/// <para>
/// It exists to take off the hot path what does not depend on the row. Before, every cell of every
/// row paid for a <see cref="Nullable.GetUnderlyingType"/> plus a
/// <see cref="PropertyInfo.SetValue(object, object)"/> — and the cost is linear in the number of
/// <b>cells</b>, not rows: a result of 10,000 rows with 9 columns performed 90,000 of those two
/// operations. Here they are resolved once per property, when the cache is built.
/// </para>
/// <para>
/// The <b>conversion</b> stays in <see cref="DaxValueConverter"/>, and on purpose: it depends on
/// the type of the value that arrived, which is only known at run time. Freezing it into compiled
/// code would duplicate the conversion rule in two places, and that is precisely the rule that
/// needed fixing twice — the invariant culture and the types <c>Convert.ChangeType</c> does not
/// reach.
/// </para>
/// </remarks>
public sealed class DaxColumnMapping
{
    internal DaxColumnMapping(PropertyInfo property)
    {
        Property = property;
        TargetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        Set = CompileSetter(property);
    }

    /// <summary>The destination property. Also used to name the property in errors.</summary>
    public PropertyInfo Property { get; }

    /// <summary>
    /// The type to convert to, already stripped of <see cref="Nullable{T}"/> — which is the form
    /// <see cref="DaxValueConverter"/> expects.
    /// </summary>
    public Type TargetType { get; }

    /// <summary>Assigns the already-converted value, without going through reflection.</summary>
    internal Action<object, object?> Set { get; }

    /// <summary>
    /// Compiles <c>((T)entity).Property = (TProp)value</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>Convert</c> of the value performs the unboxing the assignment requires, and it works
    /// for a nullable property too: the value arrives boxed as the <i>underlying</i> type, and
    /// <c>(decimal?)(object)5m</c> is a valid conversion.
    /// </para>
    /// <para>
    /// <b>A non-public setter is compiled too</b>, and that was measured, not assumed: I first
    /// wrote a fallback to <see cref="PropertyInfo.SetValue(object, object)"/> supposing the
    /// compiled delegate would not reach the accessor of a <c>{ get; private set; }</c>, and the
    /// fallback was never exercised — <see cref="LambdaExpression.Compile()"/> emits a dynamic
    /// method with no visibility check. Keeping the detour would mean carrying a path nothing
    /// executes. If some future platform starts refusing, the private-setter test fails loudly
    /// instead of silently returning the default value.
    /// </para>
    /// <para>
    /// <c>CanWrite</c> already guarantees there is a <c>set</c>, and
    /// <c>GetSetMethod(nonPublic: true)</c> returns exactly that accessor — which is why there is
    /// no null to handle here. The same goes for <see cref="MemberInfo.DeclaringType"/>: it is only
    /// null on a module-level member, and a property obtained from <c>Type.GetProperties</c> never
    /// is.
    /// </para>
    /// </remarks>
    private static Action<object, object?> CompileSetter(PropertyInfo property)
    {
        MethodInfo setter = property.GetSetMethod(nonPublic: true)!;

        ParameterExpression target = Expression.Parameter(typeof(object), "entity");
        ParameterExpression source = Expression.Parameter(typeof(object), "value");

        MethodCallExpression assignment = Expression.Call(
            Expression.Convert(target, property.DeclaringType!),
            setter,
            Expression.Convert(source, property.PropertyType));

        return Expression.Lambda<Action<object, object?>>(assignment, target, source).Compile();
    }
}
