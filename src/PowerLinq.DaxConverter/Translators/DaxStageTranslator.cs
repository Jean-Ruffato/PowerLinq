using System.Linq.Expressions;
using System.Reflection;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Translators;

/// <summary>
/// Translates an operator — the lambda's body plus the position it was applied at — into the
/// corresponding <see cref="DaxStage"/>, accumulated over a <see cref="DaxPipeline"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is not generic, and that is the point.</b> None of these translations uses the entity's
/// type: they use the lambda's <b>body</b> and the pipeline's table. While the logic lived in
/// <c>DaxQuery&lt;T&gt;</c>, the only way to reach it from an <c>IQueryProvider</c> — which knows
/// the types only as <see cref="Type"/> — would have been reflection over a generic method. Here
/// both surfaces call the same function, and the fluent API becomes a layer over this engine
/// instead of being the engine.
/// </para>
/// <para>
/// The other translators take an <see cref="Expression"/> to the DAX tree. This one takes an
/// <see cref="Expression"/> to a <b>stage</b>, and delegates the inside to them.
/// </para>
/// </remarks>
internal static class DaxStageTranslator
{
    /// <summary>
    /// Filters the rows, breaking the top-level conjunctions into one stage per table involved.
    /// </summary>
    /// <param name="pipeline">The accumulated query.</param>
    /// <param name="predicateBody">The predicate's body.</param>
    /// <param name="localizer">Language of the error messages.</param>
    public static DaxPipeline Where(
        DaxPipeline pipeline,
        Expression predicateBody,
        IPowerLinqLocalizer localizer)
    {
        IReadOnlyList<string>? result = pipeline.ResultColumns();

        foreach (Expression conjunct in Conjuncts(predicateBody))
            pipeline = AddFilter(pipeline, conjunct, result, localizer);

        return pipeline;
    }

    /// <summary>
    /// Translates predicates that will be used as filter arguments of a <c>CALCULATE</c>.
    /// </summary>
    /// <param name="tableName">The entity's table, the one the predicates were written over.</param>
    /// <param name="predicateBodies">The predicates' bodies, in declared order.</param>
    /// <param name="localizer">Language of the error messages.</param>
    /// <remarks>
    /// <para>
    /// It is the same classification path as <see cref="Where"/>: filters over the table itself
    /// iterate that table, filters over a dimension or an RLS table iterate the foreign table, and
    /// a condition that mixes tables is refused.
    /// </para>
    /// <para>
    /// Since every predicate of this call belongs to the same <c>CALCULATE</c>, conditions over the
    /// same table are merged with <c>&amp;&amp;</c>. The order of the first table found is
    /// preserved to keep the DAX deterministic.
    /// </para>
    /// </remarks>
    internal static List<IDaxTableExpression> FilterTables(
        string tableName,
        IEnumerable<Expression> predicateBodies,
        IPowerLinqLocalizer localizer)
    {
        var grouped = new List<(string TableName, IDaxExpression Predicate)>();

        foreach (Expression predicateBody in predicateBodies)
        {
            foreach (Expression conjunct in Conjuncts(predicateBody))
            {
                DaxStage stage = TranslateFilterStage(tableName, conjunct, resultColumns: null, localizer);

                (string target, IDaxExpression predicate) = stage switch
                {
                    DaxFilterStage own => (tableName, own.Predicate),
                    DaxRelatedFilterStage related => (related.TableName, related.Predicate),
                    _ => throw new InvalidOperationException(
                        $"Unexpected filter stage: {stage.GetType().Name}.")
                };

                int existing = grouped.FindIndex(
                    entry => DaxIdentifier.SameTable(entry.TableName, target));

                if (existing >= 0)
                {
                    grouped[existing] = grouped[existing] with
                    {
                        Predicate = And(grouped[existing].Predicate, predicate)
                    };
                }
                else
                {
                    grouped.Add((target, predicate));
                }
            }
        }

        return [.. grouped.Select(entry =>
            (IDaxTableExpression)new DaxFilter(new DaxTableRef(entry.TableName), entry.Predicate))];
    }

    /// <summary>
    /// Breaks the top-level conjunctions into independent predicates.
    /// </summary>
    /// <remarks>
    /// In DAX a filter applies to one table, so a predicate that talks about two tables has to
    /// become two filters. <c>&amp;&amp;</c> is the only form that allows this while preserving the
    /// meaning: the intersection of two filters is their conjunction. <c>||</c> does not split —
    /// two filters never express a union.
    /// </remarks>
    private static IEnumerable<Expression> Conjuncts(Expression body)
    {
        if (body is not BinaryExpression { NodeType: ExpressionType.AndAlso } conjunction)
        {
            yield return body;
            yield break;
        }

        foreach (Expression left in Conjuncts(conjunction.Left))
            yield return left;

        foreach (Expression right in Conjuncts(conjunction.Right))
            yield return right;
    }

    /// <summary>
    /// Translates a predicate and accumulates it on the pipeline, in the form the table that owns
    /// its columns requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FILTER</c> opens a <b>row context</b> on the table it iterates. In that context, a
    /// reference to another table's column — related or not — is a <b>DAX error</b>, not a
    /// different result: the engine answers <i>"a single value for the column cannot be
    /// determined"</i>. Before, every predicate went into the <c>FILTER</c> over the entity's
    /// table, so filtering by a dimension column or an RLS table column failed on 100% of queries —
    /// and in a star model that is the most common filter there is.
    /// </para>
    /// <para>
    /// A predicate that mixes columns of two tables in the <b>same</b> condition is refused: no
    /// filter form expresses it. Comparing columns of different tables is a case for
    /// <c>RELATED</c>, which belongs to row context and not to filter context.
    /// </para>
    /// <para>
    /// Merging predicates of the same table — which the flat definition did here, with
    /// <c>&amp;&amp;</c> — has moved: now each <c>Where</c> is a stage, and it is
    /// <c>DaxPipelineBuilder</c> that joins the consecutive ones while writing. The reason is that
    /// merging during composition would destroy the information the pipeline exists to keep: once
    /// merged, <c>Where(a).Take(5).Where(b)</c> would be indistinguishable from
    /// <c>Where(a &amp;&amp; b).Take(5)</c>.
    /// </para>
    /// </remarks>
    private static DaxPipeline AddFilter(
        DaxPipeline pipeline,
        Expression conjunct,
        IReadOnlyList<string>? resultColumns,
        IPowerLinqLocalizer localizer)
    {
        return pipeline.Then(TranslateFilterStage(
            pipeline.TableName, conjunct, resultColumns, localizer));
    }

    /// <summary>
    /// Chooses the shape of a filter for an isolated condition, without accumulating it on the
    /// pipeline yet.
    /// </summary>
    private static DaxStage TranslateFilterStage(
        string tableName,
        Expression conjunct,
        IReadOnlyList<string>? resultColumns,
        IPowerLinqLocalizer localizer)
    {
        var visitor = new DaxExpressionVisitor(tableName, localizer, resultColumns);
        IDaxExpression translated = visitor.Translate(conjunct);

        // After a reshape the result is ONE table — the one the stage produced — so there is no
        // owning-table classification to do: the filter wraps the accumulated source. A key
        // column's reference still carries the source table's name (Venda[Categoria]), and treating
        // it as "another table" would produce a CALCULATETABLE over something that is not a table.
        if (resultColumns is not null)
            return new DaxFilterStage(translated);

        string[] foreign = [.. visitor.ReferencedTables
            .Where(table => !DaxIdentifier.SameTable(table, tableName))];

        if (foreign.Length == 0)
        {
            // A navigation in a predicate that is SEPARABLE by table goes to filter context, not to
            // a FILTER with RELATED. It is not a matter of preference: RELATED inside FILTER is
            // row-by-row iteration in the formula engine, and CALCULATETABLE's boolean pushes down
            // as a filter to the storage engine — same result, costs of a different order on a
            // large fact. Emitting RELATED here would be a silent performance trap.
            if (Separable(visitor, tableName) is { } navigated)
            {
                return new DaxRelatedFilterStage(
                    navigated,
                    new DaxExpressionVisitor(tableName, localizer, resultColumns, rowContext: false)
                        .Translate(conjunct));
            }

            // What is left is the predicate that only exists in row context — a comparison between
            // columns of different tables is the anticipated case, and there RELATED is not a
            // choice.
            return new DaxFilterStage(translated);
        }

        // More than one table in the same condition, or a foreign table plus the entity's own: no
        // isolated FILTER is valid, because whichever table is chosen leaves the other column out
        // of context.
        if (foreign.Length > 1 || visitor.ReferencedTables.Count > 1)
        {
            throw new NotSupportedException(localizer.Format(
                "FilterSpansMultipleTables", string.Join(", ", visitor.ReferencedTables.Order())));
        }

        return new DaxRelatedFilterStage(foreign[0], translated);
    }

    /// <summary>
    /// The navigated table when the predicate is <b>separable</b> — it talks about <b>one</b> other
    /// table, through navigation, and about no column of its own. <see langword="null"/> when that
    /// is not the case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separable is what can become a filter-table argument. A predicate that also touches the
    /// entity's own table — <c>r.Valor &gt; r.Empresa.Limite</c> — is not: it compares columns of
    /// two tables, and no filter-context boolean expresses that. It is exactly the case navigation
    /// names as the reason navigation cannot be replaced by a context filter.
    /// </para>
    /// <para>
    /// Two navigated tables in the same condition are not separable either, for the same reason:
    /// the filter-table argument applies to one table at a time.
    /// </para>
    /// </remarks>
    private static string? Separable(DaxExpressionVisitor visitor, string tableName) =>
        visitor.NavigatedTables.Count == 1
        && !visitor.ReferencedTables.Any(
            table => DaxIdentifier.SameTable(table, tableName))
            ? visitor.NavigatedTables.First()
            : null;

    /// <summary>Structural conjunction of one table's predicates.</summary>
    private static IDaxExpression And(IDaxExpression left, IDaxExpression right) =>
        new DaxBinary(DaxOperator.And, left, right);

    /// <summary>Adds an ordering term.</summary>
    /// <param name="pipeline">The accumulated query.</param>
    /// <param name="keyBody">The key selector's body.</param>
    /// <param name="ascending">The term's direction.</param>
    /// <param name="resetsOrder">
    /// <see langword="true"/> for <c>OrderBy</c>, which replaces the accumulated ordering;
    /// <see langword="false"/> for <c>ThenBy</c>, which adds a term.
    /// </param>
    /// <param name="localizer">Language of the error messages.</param>
    /// <remarks>
    /// No window guard: ordering after a <c>Take</c> is representable, and it orders the window's
    /// result — which is what LINQ means. See <c>DaxPipelineBuilder</c> for how the pending terms
    /// are consumed.
    /// </remarks>
    public static DaxPipeline OrderBy(
        DaxPipeline pipeline,
        Expression keyBody,
        bool ascending,
        bool resetsOrder,
        IPowerLinqLocalizer localizer)
    {
        // Everything goes through the translator, including `x => x.Property`: it resolves a simple
        // property to the SAME column reference a dedicated path would produce, and it resolves
        // against the result's set when there has been a reshape.
        var term = new DaxOrderTerm(
            new DaxExpressionVisitor(pipeline.TableName, localizer, pipeline.ResultColumns())
                .Translate(keyBody),
            ascending);

        return pipeline.Then(new DaxOrderStage(term, resetsOrder));
    }

    /// <summary>Limits the number of rows — <c>TOPN</c>.</summary>
    public static DaxPipeline Take(DaxPipeline pipeline, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return pipeline.Then(new DaxTakeStage(count));
    }

    /// <summary>Discards the first rows — <c>EXCEPT(..., TOPN(n, ...))</c>.</summary>
    /// <remarks>
    /// The ordering requirement is checked at <b>generation</b> time, not here, because the
    /// ordering may be declared at any earlier position — and it is the pipeline that knows which
    /// terms are pending when this stage is reached.
    /// </remarks>
    public static DaxPipeline Skip(DaxPipeline pipeline, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return pipeline.Then(new DaxSkipStage(count));
    }

    /// <summary>Removes duplicate rows — <c>DISTINCT</c>.</summary>
    public static DaxPipeline Distinct(DaxPipeline pipeline) => pipeline.Then(new DaxDistinctStage());

    /// <summary>
    /// Projects onto another contract — <c>SELECTCOLUMNS</c>.
    /// </summary>
    /// <param name="pipeline">The accumulated query.</param>
    /// <param name="selectorBody">The selector's body: an object initializer or a construction.</param>
    /// <param name="resultType">The output contract, which becomes the pipeline's entity.</param>
    /// <param name="localizer">Language of the error messages.</param>
    public static DaxPipeline Select(
        DaxPipeline pipeline,
        Expression selectorBody,
        Type resultType,
        IPowerLinqLocalizer localizer)
    {
        var visitor = new DaxExpressionVisitor(
            pipeline.TableName, localizer, pipeline.ResultColumns());

        List<DaxProjectionColumn> columns = selectorBody switch
        {
            MemberInitExpression initializer => FromInitializer(initializer, visitor, localizer),
            NewExpression construction => FromConstruction(construction, visitor),
            _ => throw new NotSupportedException(localizer.Get("SelectInitializerRequired"))
        };

        // A projection with no columns at all — `new Contract()` or `new { }` — would generate a
        // one-argument SELECTCOLUMNS, which the server rejects. It is almost always a mistake by
        // whoever wrote it.
        if (columns.Count == 0)
            throw new NotSupportedException(localizer.Get("SelectInitializerRequired"));

        return (pipeline with { EntityType = resultType }).Then(new DaxProjectStage(columns));
    }

    /// <summary>Columns of <c>new Contract { Prop = ... }</c>.</summary>
    private static List<DaxProjectionColumn> FromInitializer(
        MemberInitExpression initializer,
        DaxExpressionVisitor visitor,
        IPowerLinqLocalizer localizer)
    {
        var columns = new List<DaxProjectionColumn>();

        foreach (MemberBinding binding in initializer.Bindings)
        {
            if (binding is not MemberAssignment { Member: PropertyInfo property } assignment)
                throw new NotSupportedException(localizer.Get("SelectPropertyAssignmentOnly"));

            columns.Add(new DaxProjectionColumn(
                DaxExpressionVisitor.OutputName(property), visitor.Translate(assignment.Expression)));
        }

        return columns;
    }

    /// <summary>
    /// Columns of <c>new { x.Id, x.Nome }</c> or <c>new Contract(x.Id, x.Nome)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The output name comes from the <b>property</b> when the compiler reports it — which is the
    /// case for an anonymous type, where <c>Members</c> comes filled in — and from the constructor
    /// parameter's name when it does not. It is the same order of preference constructor
    /// materialization uses, and it is what makes a positional <c>record</c> project and
    /// materialize under the same name.
    /// </para>
    /// <para>
    /// Without this, an anonymous type and a positional <c>record</c> fell into
    /// <c>SelectInitializerRequired</c>: neither is a <c>MemberInitExpression</c>, because neither
    /// has a writable property to initialize.
    /// </para>
    /// </remarks>
    private static List<DaxProjectionColumn> FromConstruction(
        NewExpression construction,
        DaxExpressionVisitor visitor)
    {
        ParameterInfo[] parameters = construction.Constructor?.GetParameters() ?? [];
        var columns = new List<DaxProjectionColumn>();

        for (int i = 0; i < construction.Arguments.Count; i++)
        {
            // The output name comes from the parameter, and not from `construction.Members`,
            // because the two never diverge: `Members` is only filled in for an anonymous type, and
            // there the compiler names each constructor parameter after the member. Measured — an
            // earlier version consulted `Members` first, and removing that lookup changed no test.
            string name = parameters[i].Name ?? $"Item{i}";

            columns.Add(new DaxProjectionColumn(name, visitor.Translate(construction.Arguments[i])));
        }

        return columns;
    }

    /// <summary>Output column name of a single-column projection.</summary>
    public const string ValueColumnName = "Value";

    /// <summary>
    /// The projection of <b>one</b> column, under the fixed name <c>Value</c> — the shape the value
    /// terminals materialize into <c>DaxValueRow</c>.
    /// </summary>
    public static DaxPipeline SelectValue(
        DaxPipeline pipeline,
        Expression selectorBody,
        IPowerLinqLocalizer localizer)
    {
        IDaxExpression column =
            new DaxExpressionVisitor(pipeline.TableName, localizer, pipeline.ResultColumns())
                .Translate(Unwrap(selectorBody));

        return pipeline.Then(new DaxProjectStage([new DaxProjectionColumn(ValueColumnName, column)]));
    }

    /// <summary>
    /// Discards the <c>Convert</c> the compiler inserts when the selector is typed more widely than
    /// the property — the case of <c>SumAsync&lt;decimal?&gt;(x =&gt; x.Preco)</c>.
    /// </summary>
    public static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked
            } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    /// <summary>
    /// Refuses grouping when the query already has a window (<c>Take</c> or <c>Skip</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is the <b>only</b> ordering refusal left, and its reason has changed in nature. It used
    /// to apply to <c>Where</c>, <c>OrderBy</c>, <c>Take</c>, <c>Skip</c>, <c>GroupBy</c> and
    /// <c>Aggregate</c>, and the cause was structural: the flat definition did not distinguish
    /// <c>Take(5).Where(p)</c> from <c>Where(p).Take(5)</c>, so refusing was preferable to
    /// generating DAX that answers a different question without saying so. With the pipeline that
    /// cause is gone.
    /// </para>
    /// <para>
    /// What remains is a <b>generation</b> limitation, not a representation one:
    /// <c>SUMMARIZECOLUMNS</c> takes the grouping columns and the filter tables, and has nowhere to
    /// take a <c>TOPN</c> — so grouping after a window would discard it silently. Translating that
    /// requires materializing the window in a <c>VAR</c> before grouping.
    /// </para>
    /// </remarks>
    public static void EnsureNotWindowed(
        DaxPipeline pipeline,
        string @operator,
        IPowerLinqLocalizer localizer)
    {
        foreach (DaxStage stage in pipeline.Stages)
        {
            if (stage is DaxTakeStage)
                throw new NotSupportedException(localizer.Format("OperatorAfterTake", @operator));

            if (stage is DaxSkipStage)
                throw new NotSupportedException(localizer.Format("OperatorAfterSkip", @operator));
        }
    }

    /// <summary>
    /// Refuses operators that <b>resolve columns against the source entity</b> when the query has
    /// already changed the rows' shape — through a projection, a grouping or a join.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After a <c>SELECTCOLUMNS</c> the result's columns are referenced as <c>[Name]</c>, not as
    /// <c>Table[Column]</c>. The operators that resolve against the result's set — <c>Where</c>,
    /// <c>OrderBy</c> — cross the reshape; the ones that still resolve against the source entity,
    /// such as the scalar aggregates, would generate <c>Produto[Nome]</c>: a reference the
    /// projection's result does not have, and which the server rejects.
    /// </para>
    /// <para>
    /// Refusing is preferable to emitting the wrong reference, and the distinction matters: the
    /// operators that do <b>not</b> need a column reference — <c>CountAsync</c>, <c>FirstAsync</c>,
    /// <c>SingleAsync</c>, <c>ToArrayAsync</c>, <c>ToDictionaryAsync</c>, <c>AnyAsync</c> without a
    /// predicate — work after the projection.
    /// </para>
    /// </remarks>
    public static void EnsureNotReshaped(
        DaxPipeline pipeline,
        string @operator,
        IPowerLinqLocalizer localizer)
    {
        foreach (DaxStage stage in pipeline.Stages)
        {
            if (Reshaping(stage) is { } reshaper)
                throw new NotSupportedException(localizer.Format("OperatorAfterProjection", @operator, reshaper));
        }
    }

    /// <summary>
    /// The operator that changed the rows' shape, or <see langword="null"/> when the stage does not
    /// reshape.
    /// </summary>
    /// <remarks>
    /// A join changes the shape too: the result has the output contract's columns, and a second
    /// join's key would be resolved against the source table — which is no longer the source.
    /// </remarks>
    private static string? Reshaping(DaxStage stage) => stage switch
    {
        DaxProjectStage => "Select",
        DaxGroupStage => "GroupBy",
        DaxJoinStage => "Join",
        _ => null
    };

    /// <summary>
    /// The name of the <b>operator</b> that produced the stage, for the error message.
    /// </summary>
    /// <remarks>
    /// Deriving it from the type's name would give <c>Order</c> and <c>Project</c> — stage names,
    /// which the caller never wrote. The message has to quote what is in their code.
    /// </remarks>
    public static string OperatorOf(DaxStage stage) => stage switch
    {
        DaxOrderStage => "OrderBy",
        DaxTakeStage => "Take",
        DaxSkipStage => "Skip",
        DaxProjectStage => "Select",
        DaxGroupStage => "GroupBy",
        DaxJoinStage => "Join",
        _ => stage.GetType().Name
    };
}
