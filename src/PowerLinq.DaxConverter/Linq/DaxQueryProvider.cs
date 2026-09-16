using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Mapping;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Translators;

namespace PowerLinq.DaxConverter.Linq;

/// <summary>
/// Translates LINQ's <see cref="MethodCallExpression"/> tree into the stage pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>The target is the <see cref="DaxPipeline"/>, not the DAX tree.</b>
/// <c>MethodCallExpression</c> → <see cref="DaxStage"/> is a mechanical translation: each operator
/// becomes a stage, and the one that knows how to turn a sequence of stages into an
/// <c>EVALUATE</c> is still <c>DaxPipelineBuilder</c>. Going from <see cref="Expression"/> straight
/// to DAX here would repeat the big leap that was already undone, and would duplicate the
/// generation decisions onto a second surface.
/// </para>
/// <para>
/// As a consequence, the fluent API and this one translate to the <b>same</b> representation,
/// calling the same <c>DaxStageTranslator</c> functions: an operator gained here does not have to
/// be rewritten there, and a refusal holds for both.
/// </para>
/// </remarks>
public sealed class DaxQueryProvider : IQueryProvider
{
    /// <summary>Creates the provider over the executor, with messages in English.</summary>
    /// <param name="executor">Whoever sends the DAX to the server.</param>
    public DaxQueryProvider(IDaxQueryExecutor executor)
        : this(executor, ResourceManagerPowerLinqLocalizer.English) { }

    /// <summary>Creates the provider over the executor, in the given language.</summary>
    /// <param name="executor">Whoever sends the DAX to the server.</param>
    /// <param name="localizer">Language of the error messages.</param>
    public DaxQueryProvider(IDaxQueryExecutor executor, IPowerLinqLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(localizer);

        Executor = executor;
        Localizer = localizer;
    }

    /// <summary>Whoever sends the DAX to the server.</summary>
    internal IDaxQueryExecutor Executor { get; }

    /// <summary>Language of the error messages.</summary>
    internal IPowerLinqLocalizer Localizer { get; }

    /// <summary>The whole table as a query, as the starting point for composition.</summary>
    /// <typeparam name="T">The entity, mapped by <c>[DaxTable]</c> and <c>[DaxColumn]</c>.</typeparam>
    public IQueryable<T> Root<T>() where T : class =>
        new DaxQueryable<T>(this, new DaxPipeline(EntityMapper.GetTableName<T>(), typeof(T)));

    /// <inheritdoc/>
    /// <remarks>
    /// It translates <b>now</b>, not at the terminal: that is what makes an untranslatable operator
    /// blow up on the line where it was written.
    /// </remarks>
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return new DaxQueryable<TElement>(this, Translate(expression), expression);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The non-generic form is the one third-party libraries use when they build the query by
    /// reflection — dynamic filtering and OData, among others. It exists for that, and delegates to
    /// the generic one.
    /// </remarks>
    public IQueryable CreateQuery(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        Type element = ElementTypeOf(expression.Type)
            ?? throw new NotSupportedException(
                Localizer.Format("QueryableExpressionUnsupported", expression.Type.Name));

        try
        {
            return (IQueryable)GenericCreateQuery
                .MakeGenericMethod(element)
                .Invoke(this, [expression])!;
        }
        catch (TargetInvocationException invocation) when (invocation.InnerException is not null)
        {
            // Without this, a translation refusal would reach the caller wrapped in a
            // TargetInvocationException — the message that matters disappears from the top of the
            // stack.
            ExceptionDispatchInfo.Capture(invocation.InnerException).Throw();
            throw;
        }
    }

    private static readonly MethodInfo GenericCreateQuery = typeof(DaxQueryProvider)
        .GetMethods()
        .Single(method => method.Name == nameof(CreateQuery) && method.IsGenericMethodDefinition);

    /// <summary>The element type of <c>IQueryable&lt;X&gt;</c>, or <see langword="null"/>.</summary>
    private static Type? ElementTypeOf(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>)
            ? type.GetGenericArguments()[0]
            : type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryable<>))
                ?.GetGenericArguments()[0];

    /// <summary>
    /// <b>Always throws.</b> There is no synchronous execution — see
    /// <see cref="DaxQueryable{T}.GetEnumerator"/>.
    /// </summary>
    /// <param name="expression">The query, used only to name the equivalent async terminal.</param>
    /// <exception cref="NotSupportedException">Always.</exception>
    public object? Execute(Expression expression) => throw SyncExecution(expression);

    /// <summary>
    /// <b>Always throws.</b> There is no synchronous execution — see
    /// <see cref="DaxQueryable{T}.GetEnumerator"/>.
    /// </summary>
    /// <typeparam name="TResult">The type the operator would return.</typeparam>
    /// <param name="expression">The query, used only to name the equivalent async terminal.</param>
    /// <exception cref="NotSupportedException">Always.</exception>
    public TResult Execute<TResult>(Expression expression) => throw SyncExecution(expression);

    /// <summary>
    /// The refusal of synchronous execution, naming the corresponding async terminal when one
    /// exists.
    /// </summary>
    /// <remarks>
    /// <c>Count()</c> and <c>CountAsync()</c> differ by a suffix, and a generic message would leave
    /// the caller hunting for the right method. A terminal with no equivalent — <c>Last</c>,
    /// <c>ElementAt</c> — falls back to the general message, because there is nothing to suggest.
    /// </remarks>
    private Exception SyncExecution(Expression expression)
    {
        string? @operator = (expression as MethodCallExpression)?.Method.Name;

        return @operator is not null && AsyncTerminals.Contains(@operator)
            ? new NotSupportedException(
                Localizer.Format("QueryableSyncTerminal", @operator, @operator + "Async"))
            : new NotSupportedException(Localizer.Get("QueryableSyncEnumeration"));
    }

    /// <summary>The LINQ terminals whose async equivalent is the name plus <c>Async</c>.</summary>
    /// <remarks>
    /// Only the ones that come through here: <c>ToList</c> and <c>ToArray</c> belong to
    /// <see cref="Enumerable"/> and land in <see cref="DaxQueryable{T}.GetEnumerator"/>, whose
    /// message already names them. A terminal with no equivalent — <c>Last</c>, <c>ElementAt</c>,
    /// <c>Contains</c> — is deliberately absent: suggesting a name that does not exist is worse
    /// than the general message.
    /// </remarks>
    private static readonly HashSet<string> AsyncTerminals =
    [
        "All", "Any", "Average", "Count", "First", "FirstOrDefault", "LongCount", "Max", "Min",
        "Single", "SingleOrDefault", "Sum"
    ];

    /// <summary>The query the expression represents, translated into the pipeline.</summary>
    internal DaxPipeline Translate(Expression expression) => expression switch
    {
        // The source of a tree built by a third party is a constant with the queryable inside.
        // Reading its pipeline — instead of retranslating the expression it carries — is what lets
        // an already-composed fluent query come in as the root.
        ConstantExpression { Value: IDaxQueryable source } => source.Pipeline,

        MethodCallExpression call => Apply(call),

        _ => throw new NotSupportedException(
            Localizer.Format("QueryableExpressionUnsupported", expression.NodeType))
    };

    /// <summary>Applies a LINQ operator over the source that precedes it.</summary>
    /// <remarks>
    /// The dispatch is by <b>name</b>, not by <see cref="MemberInfo.DeclaringType"/>: a dynamic
    /// filtering library builds the call with <c>Expression.Call</c> and does not always point at
    /// <see cref="Queryable"/>. What has to match is the shape — source in the first argument,
    /// lambda in the second.
    /// </remarks>
    private DaxPipeline Apply(MethodCallExpression call)
    {
        if (call.Object is null && call.Arguments.Count == 0)
            throw Unsupported(call);

        DaxPipeline source = Translate(call.Object ?? call.Arguments[0]);

        return call.Method.Name switch
        {
            "Where" => DaxStageTranslator.Where(source, Selector(call).Body, Localizer),

            "OrderBy" => Order(source, call, ascending: true, resetsOrder: true),
            "OrderByDescending" => Order(source, call, ascending: false, resetsOrder: true),
            "ThenBy" => Order(source, call, ascending: true, resetsOrder: false),
            "ThenByDescending" => Order(source, call, ascending: false, resetsOrder: false),

            "Take" => DaxStageTranslator.Take(source, Window(call)),
            "Skip" => DaxStageTranslator.Skip(source, Window(call)),

            // The comparer of the other overload is client code deciding equality per row, and
            // DAX's DISTINCT compares by the columns' values.
            "Distinct" => Arguments(call).Count == 0
                ? DaxStageTranslator.Distinct(source)
                : throw Overload(call),

            "Select" => Select(source, call),

            _ => throw Unsupported(call)
        };
    }

    private DaxPipeline Order(
        DaxPipeline source,
        MethodCallExpression call,
        bool ascending,
        bool resetsOrder) =>
        DaxStageTranslator.OrderBy(source, Selector(call).Body, ascending, resetsOrder, Localizer);

    /// <summary>
    /// The projection, refusing the one that produces a <b>value</b> instead of a contract.
    /// </summary>
    /// <remarks>
    /// <c>select p.Nome</c> would return an <c>IQueryable&lt;string&gt;</c>, and this library's
    /// materialization builds objects: a bare column has no contract to land on. The fluent API
    /// solves that with terminals of its own — <c>ValuesAsync</c> and <c>DistinctValuesAsync</c> —
    /// and the message names them instead of leaving the error to the server.
    /// </remarks>
    private DaxPipeline Select(DaxPipeline source, MethodCallExpression call)
    {
        LambdaExpression selector = Selector(call);
        Type result = selector.ReturnType;

        if (result.IsValueType || result == typeof(string))
            throw new NotSupportedException(Localizer.Format("QueryableScalarProjection", result.Name));

        return DaxStageTranslator.Select(source, selector.Body, result, Localizer);
    }

    /// <summary>The arguments after the source, whether it is the object or the first of them.</summary>
    private static IReadOnlyList<Expression> Arguments(MethodCallExpression call) =>
        call.Object is null ? [.. call.Arguments.Skip(1)] : call.Arguments;

    /// <summary>
    /// The operator's lambda, refusing the overloads that take the row's <b>index</b>.
    /// </summary>
    /// <remarks>
    /// <c>Where((x, i) =&gt; ...)</c> and <c>Select((x, i) =&gt; ...)</c> depend on the row's
    /// position in the sequence, and in DAX there is no position: the result of a table expression
    /// is a set, and order only exists in the <c>EVALUATE</c>'s <c>ORDER BY</c> clause. Translating
    /// while ignoring the index would produce a query that answers a different question.
    /// </remarks>
    private LambdaExpression Selector(MethodCallExpression call)
    {
        IReadOnlyList<Expression> arguments = Arguments(call);

        if (arguments.Count != 1 || Unquote(arguments[0]) is not LambdaExpression lambda)
            throw Overload(call);

        return lambda.Parameters.Count == 1
            ? lambda
            : throw new NotSupportedException(
                Localizer.Format("QueryableIndexedOverload", call.Method.Name));
    }

    /// <summary>The window size of <c>Take</c>/<c>Skip</c>.</summary>
    /// <remarks>
    /// It accepts a constant and a captured variable — both are known when the query is composed,
    /// and refusing the second would rule out the common case of the page size coming from a
    /// parameter. The overload that takes a <see cref="Range"/> does not get past here: the
    /// argument is not an <see cref="int"/>, and a window counted from the end would require
    /// knowing the row count.
    /// </remarks>
    private int Window(MethodCallExpression call)
    {
        IReadOnlyList<Expression> arguments = Arguments(call);

        if (arguments.Count != 1 || arguments[0].Type != typeof(int))
            throw Overload(call);

        return arguments[0] is ConstantExpression { Value: int constant }
            ? constant
            : (int)Expression.Lambda(arguments[0]).Compile().DynamicInvoke()!;
    }

    /// <summary>
    /// The refusal of an <b>overload</b> of an operator that, in its common form, does translate.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Unsupported"/> because that one's message lists the supported
    /// operators — and seeing <c>Take</c> in the list right below "Take has no translation" would
    /// make the reader doubt what is written. What does not translate here is the form:
    /// <c>Take(Range)</c> would need to know the row count, and a comparer is client code running
    /// per row.
    /// </remarks>
    private Exception Overload(MethodCallExpression call) =>
        new NotSupportedException(Localizer.Format("QueryableOverloadUnsupported", call.Method.Name));

    private static Expression Unquote(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Quote } quote
            ? quote.Operand
            : expression;

    /// <summary>
    /// The refusal of a LINQ operator that has no translation, naming it.
    /// </summary>
    /// <remarks>
    /// The message quotes the operator because that is what is written in the caller's code. An
    /// untranslatable member — <c>Nome.Length</c> and the like — is named one layer below, by
    /// <c>DaxExpressionVisitor</c>, which is the same path for both surfaces.
    /// </remarks>
    private Exception Unsupported(MethodCallExpression call) =>
        new NotSupportedException(Localizer.Format("QueryableOperatorUnsupported", call.Method.Name));
}
