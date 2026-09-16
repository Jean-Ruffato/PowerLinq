using PowerLinq.DaxConverter.Context;
using PowerLinq.DaxConverter.Execution;

namespace PowerLinq.Extensions.DependencyInjection;

/// <summary>
/// Creates contexts pointed at a target resolved at run time.
/// </summary>
/// <remarks>
/// <para>
/// This is the preferred design for multi-tenant because it keeps the context's mental model: the
/// target is resolved once, at the edge of the request, and from there inwards
/// <c>Set&lt;T&gt;()</c>, <c>Where</c> and <c>GroupBy</c> carry no extra parameter. The
/// alternative — a target on every execution call — would leak the target through the whole query
/// composition chain, which has nothing to do with where the query runs.
/// </para>
/// <para>
/// The context obtained here is <b>not</b> the container's: it is a new instance, with its own
/// executor. That is the expected behaviour — two targets cannot share a context, because the
/// executor is what carries the target.
/// </para>
/// </remarks>
public interface IDaxContextFactory
{
    /// <summary>Creates a context that queries <paramref name="target"/>.</summary>
    /// <exception cref="ArgumentException">The target has an empty workspace or dataset.</exception>
    TContext Create<TContext>(DaxTarget target) where TContext : DaxContext;
}
