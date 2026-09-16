using System.Collections.Frozen;
using System.Linq.Expressions;
using PowerLinq.DaxConverter.Builders;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Mapping;
using PowerLinq.DaxConverter.Syntax;
using PowerLinq.DaxConverter.Translators;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// Represents an immutable, composable DAX query. Each method returns a new instance.
/// </summary>
public sealed class DaxQuery<T> where T : class
{
    private readonly IDaxQueryExecutor _executor;
    private readonly DaxPipeline _pipeline;
    private readonly IPowerLinqLocalizer _localizer;

    internal DaxQuery(
        IDaxQueryExecutor executor,
        DaxPipeline pipeline,
        IPowerLinqLocalizer localizer)
    {
        _executor = executor;
        _pipeline = pipeline;
        _localizer = localizer;
    }

    /// <remarks>
    /// The three pieces of state, so the <c>IQueryable</c> surface can build the same query without
    /// duplicating anything — see <c>DaxQueryableExtensions</c>.
    /// </remarks>
    internal IDaxQueryExecutor Executor => _executor;

    /// <inheritdoc cref="Executor"/>
    internal DaxPipeline Pipeline => _pipeline;

    /// <inheritdoc cref="Executor"/>
    internal IPowerLinqLocalizer Localizer => _localizer;

    /// <summary>
    /// Filters the rows. A predicate over a column of another table of the model is accepted and
    /// emitted as filter context — see <c>DaxStageTranslator</c>.
    /// </summary>
    /// <remarks>
    /// It can be applied <b>at any position</b>, including after a <c>Take</c> or a <c>Skip</c>:
    /// each operator is a stage in the sequence, so filtering a window's result is another query —
    /// and it is the one LINQ means — instead of being unrepresentable.
    /// </remarks>
    public DaxQuery<T> Where(Expression<Func<T, bool>> predicate) =>
        new(_executor, DaxStageTranslator.Where(_pipeline, predicate.Body, _localizer), _localizer);

    /// <summary>
    /// Applies <see cref="Where"/> only when <paramref name="condition"/> is true; otherwise it
    /// returns the query unchanged. It avoids the <c>if</c> around optional filters (the "only
    /// filter if the parameter came in" pattern).
    /// </summary>
    public DaxQuery<T> WhereIf(bool condition, Expression<Func<T, bool>> predicate) =>
        condition ? Where(predicate) : this;

    /// <summary>
    /// Applies <see cref="Where"/> only when <paramref name="value"/> has content (not null and not
    /// only whitespace). A convenience for filters coming from optional text fields:
    /// <c>.WhereIf(company, x => x.Exporter == company)</c>.
    /// </summary>
    public DaxQuery<T> WhereIf(string? value, Expression<Func<T, bool>> predicate) =>
        WhereIf(!string.IsNullOrWhiteSpace(value), predicate);

    /// <summary>Sorts ascending — <c>ORDER BY ... ASC</c>.</summary>
    /// <remarks>
    /// It <b>replaces</b> the accumulated ordering, as in LINQ. To add a criterion, use
    /// <see cref="ThenBy{TKey}"/>.
    /// </remarks>
    public DaxQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) =>
        AddOrderBy(keySelector.Body, ascending: true, resetsOrder: true);

    /// <summary>Sorts descending — <c>ORDER BY ... DESC</c>.</summary>
    /// <remarks>It replaces the accumulated ordering, like <see cref="OrderBy{TKey}"/>.</remarks>
    public DaxQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) =>
        AddOrderBy(keySelector.Body, ascending: false, resetsOrder: true);

    /// <summary>An additional ordering term, ascending.</summary>
    public DaxQuery<T> ThenBy<TKey>(Expression<Func<T, TKey>> keySelector) =>
        AddOrderBy(keySelector.Body, ascending: true, resetsOrder: false);

    /// <summary>An additional ordering term, descending.</summary>
    public DaxQuery<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector) =>
        AddOrderBy(keySelector.Body, ascending: false, resetsOrder: false);

    /// <summary>Limits the number of rows — <c>TOPN</c>.</summary>
    /// <remarks>
    /// Chaining a <c>Take</c> over a <c>Take</c> became representable and turns into a nested
    /// <c>TOPN</c>, which is what <c>Take(10).Take(5)</c> means in LINQ. It used to be refused
    /// because the flat definition had a single field for the window, so the second value would
    /// overwrite the first.
    /// </remarks>
    public DaxQuery<T> Take(int count) =>
        new(_executor, DaxStageTranslator.Take(_pipeline, count), _localizer);

    /// <summary>
    /// Discards the first rows. It requires an ordering declared beforehand, since pagination
    /// without order is not deterministic.
    /// </summary>
    /// <remarks>
    /// The ordering requirement is checked at <b>generation</b> time, not here, because the
    /// ordering may be declared at any earlier position — and it is the pipeline that knows which
    /// terms are pending when this stage is reached.
    /// </remarks>
    public DaxQuery<T> Skip(int count) =>
        new(_executor, DaxStageTranslator.Skip(_pipeline, count), _localizer);

    /// <summary>
    /// Projects onto another contract — <c>SELECTCOLUMNS</c>. The selector has to be an object
    /// initializer; each column's name comes from the property name, and <c>[DaxColumn]</c> is
    /// there to rename it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It returns a <see cref="DaxQuery{TResult}"/>, not a terminal class of its own: the
    /// projection is a pipeline stage, so <b>every</b> execution operator applies after it —
    /// <c>CountAsync</c>, <c>FirstAsync</c>, <c>SingleAsync</c>, <c>ToArrayAsync</c>,
    /// <c>ToDictionaryAsync</c>. There used to be two: <c>ToListAsync</c> and
    /// <c>FirstOrDefaultAsync</c>.
    /// </para>
    /// <para>
    /// What still does not apply is composing an operator that resolves columns against the source
    /// entity — see <c>DaxStageTranslator.EnsureNotReshaped</c>.
    /// </para>
    /// </remarks>
    public DaxQuery<TResult> Select<TResult>(Expression<Func<T, TResult>> selector)
        where TResult : class =>
        new(_executor,
            DaxStageTranslator.Select(_pipeline, selector.Body, typeof(TResult), _localizer),
            _localizer);

    /// <summary>Groups by a key; the following <c>Select</c> defines the aggregations.</summary>
    /// <remarks>
    /// The resulting <c>SUMMARIZECOLUMNS</c> only makes use of the query's filter, and would
    /// silently discard <c>Take</c>, <c>Skip</c> and the ordering — which is why grouping after a
    /// window is still refused (see <c>DaxStageTranslator.EnsureNotWindowed</c>). It is the refusal
    /// that <b>remains</b> after the change of representation: it does not come from the structure,
    /// it comes from <c>SUMMARIZECOLUMNS</c> not taking the window.
    /// </remarks>
    public DaxGroupedQuery<T, TKey> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        DaxStageTranslator.EnsureNotWindowed(_pipeline, nameof(GroupBy), _localizer);
        DaxStageTranslator.EnsureNotReshaped(_pipeline, nameof(GroupBy), _localizer);

        return new(_executor, _pipeline, keySelector, _localizer);
    }

    /// <summary>
    /// Aggregates without a key: a single row with the extension columns
    /// (a <c>SUMMARIZECOLUMNS</c> with no grouping column).
    /// </summary>
    /// <remarks>The same window restriction described in <see cref="GroupBy"/> applies.</remarks>
    public DaxQuery<TResult> Aggregate<TResult>(
        Expression<Func<IDaxAggregate<T>, TResult>> selector) where TResult : class
    {
        DaxStageTranslator.EnsureNotWindowed(_pipeline, nameof(Aggregate), _localizer);
        DaxStageTranslator.EnsureNotReshaped(_pipeline, nameof(Aggregate), _localizer);

        List<DaxAggregateColumn> extensions = DaxAggregationTranslator.TranslateProjection(
            selector, _pipeline.TableName, _localizer);

        DaxPipeline grouped = (_pipeline with { EntityType = typeof(TResult) })
            .Then(new DaxGroupStage([], extensions));

        return new DaxQuery<TResult>(_executor, grouped, _localizer);
    }

    /// <summary>Equijoin with another table — <c>GENERATE</c>, <c>FILTER</c> and <c>SELECTCOLUMNS</c>.</summary>
    /// <param name="inner">The inner side's table. It has to come from <c>IDaxTableFactory</c>.</param>
    /// <param name="outerKeySelector">This side's key — a property.</param>
    /// <param name="innerKeySelector">The inner side's key — a property.</param>
    /// <param name="resultSelector">The result's object initializer.</param>
    /// <remarks>
    /// <para>
    /// The outer side is the accumulated query, so <c>Where(...).Join(...)</c> joins the
    /// <b>already-filtered</b> table — which was not possible while the join was a terminal class
    /// created from the bare table.
    /// </para>
    /// <para>
    /// It redoes the join by the given key, not by the model's relationship. In a semantic model,
    /// grouping by a dimension column and summing a fact column already resolves the join through
    /// the relationships, with no <c>Join</c>.
    /// </para>
    /// </remarks>
    public DaxQuery<TResult> Join<TInner, TKey, TResult>(
        IDaxTable<TInner> inner,
        Expression<Func<T, TKey>> outerKeySelector,
        Expression<Func<TInner, TKey>> innerKeySelector,
        Expression<Func<T, TInner, TResult>> resultSelector)
        where TInner : class
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (inner is not DaxTable<TInner> innerTable)
            throw new NotSupportedException(_localizer.Get("JoinFactoryRequired"));

        return Join(
            new DaxQuery<TInner>(
                _executor,
                new DaxPipeline(innerTable.TableName, typeof(TInner)),
                _localizer),
            outerKeySelector,
            innerKeySelector,
            resultSelector);
    }

    /// <summary>
    /// Equijoin with a query — the inner side may come in <b>filtered</b>.
    /// </summary>
    /// <param name="inner">The inner side's query. Only filters are accepted on it.</param>
    /// <param name="outerKeySelector">This side's key; <c>new { a, b }</c> for a composite key.</param>
    /// <param name="innerKeySelector">The inner side's key, with the same number of components.</param>
    /// <param name="resultSelector">The result's object initializer.</param>
    /// <remarks>
    /// <para>
    /// The inner side goes in as a <b>table to cross with</b>, so only a filter is accepted on it:
    /// ordering or limiting before the cross would change which rows take part in it without the
    /// generated DAX expressing that. Refusing during composition is preferable to discarding
    /// silently.
    /// </para>
    /// </remarks>
    public DaxQuery<TResult> Join<TInner, TKey, TResult>(
        DaxQuery<TInner> inner,
        Expression<Func<T, TKey>> outerKeySelector,
        Expression<Func<TInner, TKey>> innerKeySelector,
        Expression<Func<T, TInner, TResult>> resultSelector)
        where TInner : class
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(inner);

        foreach (DaxStage stage in inner._pipeline.Stages)
        {
            if (stage is DaxFilterStage or DaxRelatedFilterStage)
                continue;

            throw new NotSupportedException(
                _localizer.Format("JoinInnerFilterOnly", DaxStageTranslator.OperatorOf(stage)));
        }

        DaxJoinStage joinStage = DaxJoinTranslator.Translate(
            _pipeline.TableName,
            inner._pipeline.TableName,
            inner._pipeline.Stages,
            _pipeline.ResultColumns(),
            outerKeySelector,
            innerKeySelector,
            resultSelector,
            _localizer);

        DaxPipeline joined = (_pipeline with { EntityType = typeof(TResult) }).Then(joinStage);

        return new DaxQuery<TResult>(_executor, joined, _localizer);
    }

    /// <summary>Returns the DAX query string that would be sent to the server.</summary>
    public string ToDaxString() => DaxPipelineBuilder.Build(_pipeline, _localizer);

    /// <summary>Returns the DAX tree before writing, without executing the query.</summary>
    public DaxEvaluate ToSyntaxTree() => DaxPipelineBuilder.BuildSyntax(_pipeline, _localizer);

    /// <summary>Runs the query and materializes every row.</summary>
    public async Task<List<T>> ToListAsync(CancellationToken cancellationToken = default)
    {
        string query = DaxPipelineBuilder.Build(_pipeline, _localizer);
        return await _executor.ExecuteAsync<T>(query, cancellationToken);
    }

    /// <summary>Reads a <b>model measure</b>, with the composed filters as context.</summary>
    /// <typeparam name="TValue">The result's type.</typeparam>
    /// <param name="measure">The measure's name, with or without brackets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="NotSupportedException">
    /// An earlier stage changed the result's shape — a window, a projection, a grouping or a join.
    /// </exception>
    /// <remarks>
    /// <para>
    /// In Power BI the business logic lives in the measures: <c>[Total Vendas]</c> and
    /// <c>[Margem %]</c> have already been written and reviewed by the BI team, and they are the
    /// official source of the number that appears in the report. Recomputing that in C# duplicates
    /// the rule and risks diverging from the report — the worst kind of bug in an indicators
    /// context, because the number looks plausible.
    /// </para>
    /// <para>
    /// <b>A measure is not a column.</b> A column exists per row and needs an iterator — that is
    /// why <see cref="SumAsync{TValue}"/> emits <c>SUMX</c> and not <c>SUM</c>. A measure already
    /// is the aggregation, and the filters go in as <c>CALCULATE</c>, that is, as the slice to
    /// evaluate it over.
    /// </para>
    /// </remarks>
    public Task<TValue?> MeasureAsync<TValue>(
        string measure,
        CancellationToken cancellationToken = default) =>
        MeasureAsync<TValue>(measure, [], cancellationToken);

    /// <summary>
    /// The measure with the filter of the given columns <b>removed</b> —
    /// <c>CALCULATE([Measure], REMOVEFILTERS(column...))</c>.
    /// </summary>
    /// <typeparam name="TValue">The result's type.</typeparam>
    /// <param name="measure">The measure's name, with or without brackets.</param>
    /// <param name="columns">
    /// The columns whose filter goes away. <b>None</b> removes the filter from the whole table.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// It is the denominator of a ratio: the measure with the slice, divided by the measure without
    /// it. The composed <c>Where</c> still goes in as filter context, and the modifier <b>undoes</b>
    /// the part of it that talks about the given columns.
    /// </para>
    /// <para>
    /// <b>The parameter is a column selector, not a predicate.</b> Inside <c>CALCULATE</c> a
    /// predicate <b>applies</b> filter and a modifier <b>removes</b> it — the same syntactic
    /// position, the inverse effect — so passing <c>v =&gt; v.Uf == "SP"</c> here is refused while
    /// naming that difference. To apply a filter, compose a <c>Where</c>.
    /// </para>
    /// </remarks>
    public Task<TValue?> MeasureIgnoringAsync<TValue>(
        string measure,
        Expression<Func<T, object?>>[] columns,
        CancellationToken cancellationToken = default) =>
        MeasureAsync<TValue>(measure, [RemoveFilters(columns)], cancellationToken);

    /// <summary>
    /// The measure keeping the filter of <b>only</b> the given columns —
    /// <c>CALCULATE([Measure], ALLEXCEPT(Table, column...))</c>.
    /// </summary>
    /// <typeparam name="TValue">The result's type.</typeparam>
    /// <param name="measure">The measure's name, with or without brackets.</param>
    /// <param name="columns">The columns whose filter <b>stays</b>. At least one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The list is of what <b>survives</b>. The name states the intent; the generated
    /// <c>ALLEXCEPT</c> describes the mechanics, and a naive reading of it suggests the opposite —
    /// which is why the API does not repeat the function's name.
    /// </remarks>
    public Task<TValue?> MeasureKeepingOnlyAsync<TValue>(
        string measure,
        Expression<Func<T, object?>>[] columns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Length == 0)
            throw new NotSupportedException(_localizer.Get("KeepFiltersNeedsAColumn"));

        return MeasureAsync<TValue>(
            measure,
            [new DaxAllExcept(DaxIdentifier.Quote(_pipeline.TableName), Columns(columns))],
            cancellationToken);
    }

    /// <summary>
    /// <c>REMOVEFILTERS</c> of the columns, or of the table when none is given.
    /// </summary>
    /// <remarks>
    /// The table rather than <c>REMOVEFILTERS()</c>: the latter removes the filter from
    /// <b>everything</b>, including tables the query never mentions, and that is far too powerful to
    /// come out of an omitted argument.
    /// </remarks>
    private DaxRemoveFilters RemoveFilters(Expression<Func<T, object?>>[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        return new DaxRemoveFilters(
            columns.Length == 0
                ? [DaxIdentifier.Quote(_pipeline.TableName)]
                : Columns(columns));
    }

    private List<string> Columns(Expression<Func<T, object?>>[] columns) =>
        [.. columns.Select(column => DaxFilterModifierTranslator.Column(
            column.Body, _pipeline.TableName, _localizer))];

    private async Task<TValue?> MeasureAsync<TValue>(
        string measure,
        IReadOnlyList<IDaxTableExpression> modifiers,
        CancellationToken cancellationToken)
    {
        string query = DaxPipelineBuilder.BuildMeasure(_pipeline, measure, modifiers, _localizer);
        object? value = await _executor.ExecuteScalarAsync(query, cancellationToken);

        // BLANK is not zero: a measure over an empty slice returns BLANK, and flattening it to zero
        // would erase the difference between "summed to zero" and "there was no row" — the same
        // decision as MinAsync and AverageOrDefaultAsync.
        return value is null
            ? default
            : (TValue)DaxValueConverter.Convert(
                value,
                Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue),
                measure,
                measure,
                _localizer);
    }

    /// <summary>
    /// The rows <b>as they arrive</b>, without materializing the list.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="NotSupportedException">
    /// The executor does not implement <see cref="IDaxStreamingQueryExecutor"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <c>ToListAsync</c> materializes the whole result before the caller sees the first row. On a
    /// large report over a fact table that is the process's memory peak — and the way of reading
    /// was never the problem: the executor already reads through an <c>AdomdDataReader</c>, and
    /// what materialized was the loop draining the reader into a list.
    /// </para>
    /// <para>
    /// <b>An explicit boundary.</b> After this there is no more composition: what comes back is a
    /// sequence, and any filtering or ordering starts happening on the client. That is why the
    /// method has a name of its own rather than the query being enumerable by itself.
    /// </para>
    /// <para>
    /// Abandoning the enumeration partway releases what the read was holding — the rented
    /// connection, on the pool path. It is <c>await foreach</c> that guarantees this, calling the
    /// enumerator's <c>DisposeAsync</c> when it leaves through a <c>break</c> or an exception.
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<T> AsAsyncEnumerable(CancellationToken cancellationToken = default)
    {
        if (_executor is not IDaxStreamingQueryExecutor streaming)
            throw new NotSupportedException(_localizer.Get("StreamingRequiresStreamingExecutor"));

        return streaming.ExecuteStreamAsync<T>(
            DaxPipelineBuilder.Build(_pipeline, _localizer), cancellationToken);
    }

    /// <summary>
    /// The page <b>and</b> the total of the whole set, in a single query.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="NotSupportedException">
    /// The executor does not implement <see cref="IDaxRawQueryExecutor"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A paged grid needs both, and doing them in two trips to the endpoint costs two connections,
    /// two capacity queues and two scans — besides opening an inconsistency window: a refresh
    /// between them returns a page from one state and a total from another.
    /// </para>
    /// <para>
    /// The total is that of the set <b>before</b> <c>Skip</c>/<c>Take</c>. A page past the end
    /// returns an empty <c>Items</c> and the total is still the set's — which is exactly the case
    /// where a grid needs the number to know that the requested page does not exist.
    /// </para>
    /// <para>
    /// It requires <see cref="IDaxRawQueryExecutor"/> because the page and the total arrive on the
    /// same row: the total is a column stamped on by <c>ADDCOLUMNS</c>, and materializing straight
    /// onto the contract would drop it. An executor that does not implement it keeps serving
    /// everything else — the refusal names the reason instead of returning a page with no total.
    /// </para>
    /// </remarks>
    public async Task<DaxPage<T>> ToPagedListAsync(CancellationToken cancellationToken = default)
    {
        if (_executor is not IDaxRawQueryExecutor raw)
            throw new NotSupportedException(_localizer.Get("PagingRequiresRawExecutor"));

        string query = DaxPipelineBuilder.BuildPaged(_pipeline, _localizer);
        DaxResult result = await raw.ExecuteRowsAsync(query, cancellationToken);

        FrozenDictionary<string, DaxColumnMapping> mappings = EntityMapper.GetColumnMappings(typeof(T));

        var items = new List<T>(result.Rows.Count);

        foreach (DaxRow row in result.Rows)
            items.Add(EntityMapper.MapRow<T>(row, mappings, _localizer));

        return new DaxPage<T>(items, await TotalOfAsync(result, cancellationToken));
    }

    /// <summary>Reads the total stamped on the page, or fetches it when the page came back empty.</summary>
    /// <remarks>
    /// <para>
    /// <c>ADDCOLUMNS</c> stamps the total on <b>every</b> row of the page, with the same value —
    /// reading it from the first is reading it from any of them. But <b>an empty page has nowhere
    /// to carry the stamp</b>, and that is precisely the case of a <c>Skip</c> past the end: zero
    /// rows and a total that is still greater than zero.
    /// </para>
    /// <para>
    /// The originally proposed shape — one <c>EVALUATE</c> with <c>ADDCOLUMNS</c> — cannot express
    /// that, because it puts the total <i>on the rows</i>. Here an empty page pays for a second
    /// trip, with the same count <c>CountAsync</c> emits. The alternative would be a second
    /// <c>EVALUATE</c> in the same command, returning two result sets — the executor would have to
    /// read <c>NextResult</c>, and there is no way to verify here whether the XMLA endpoint accepts
    /// that shape.
    /// </para>
    /// <para>
    /// The normal case is still <b>one</b> query: the second only happens when the requested page
    /// is past the end, which is the degenerate case.
    /// </para>
    /// </remarks>
    private async Task<long> TotalOfAsync(DaxResult result, CancellationToken cancellationToken)
    {
        if (result.Rows.Count == 0)
            return await LongCountAsync(cancellationToken);

        object? cell = result.Rows[0][DaxPipelineBuilder.TotalColumn];

        return cell is null
            ? 0
            : (long)DaxValueConverter.Convert(
                cell, typeof(long), DaxPipelineBuilder.TotalColumn,
                DaxPipelineBuilder.TotalColumn, _localizer);
    }

    /// <summary>
    /// First row, or <see langword="null"/> when there is none. It applies <c>TOPN(1, ...)</c> on
    /// the server, instead of fetching the whole result.
    /// </summary>
    public async Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        // Take(1) as a stage: after an earlier window it becomes a nested TOPN, which is what
        // Take(5).First() means — the first of the five.
        string query = DaxPipelineBuilder.Build(_pipeline.Then(new DaxTakeStage(1)), _localizer);
        List<T> results = await _executor.ExecuteAsync<T>(query, cancellationToken);
        return results.Count > 0 ? results[0] : default;
    }

    /// <summary>First row; throws when the result is empty.</summary>
    public async Task<T> FirstAsync(CancellationToken cancellationToken = default)
    {
        T? result = await FirstOrDefaultAsync(cancellationToken);
        return result ?? throw new InvalidOperationException(_localizer.Get("SequenceEmpty"));
    }

    /// <summary>Removes duplicate rows on the server — <c>DISTINCT</c>.</summary>
    /// <remarks>
    /// Over the whole entity, deduplication is rarely what is wanted, because a key column makes
    /// every row unique. The natural use is after a <see cref="Select{TResult}"/> that reduced the
    /// columns — and, for a single column, <see cref="DistinctValuesAsync{TValue}"/> spares the
    /// intermediate contract.
    /// </remarks>
    public DaxQuery<T> Distinct() =>
        new(_executor, DaxStageTranslator.Distinct(_pipeline), _localizer);

    /// <summary>
    /// The distinct values of a column, <b>with no intermediate contract</b> — a filter's option
    /// list, which is the use that motivated this.
    /// </summary>
    /// <param name="selector">The column.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// It composes a single-column projection and a <c>DISTINCT</c>, so everything that holds for
    /// those two holds here: the earlier filter goes in, and the earlier ordering is rewritten onto
    /// the projected column — <c>OrderBy(x =&gt; x.Uf).DistinctValuesAsync(x =&gt; x.Uf)</c>
    /// returns the list sorted by the server.
    /// </para>
    /// <para>
    /// The contract that disappears was real: an option list required declaring a type with a
    /// single property, whose only reason to exist was that there was no way to project a column.
    /// </para>
    /// </remarks>
    public Task<List<TValue>> DistinctValuesAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) =>
        ValuesAsync(selector, distinct: true, cancellationToken);

    /// <summary>
    /// The values of a column, <b>with no intermediate contract</b> and with no deduplication.
    /// </summary>
    /// <param name="selector">The column.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// It is the scalar projection. <c>Select</c> does not serve this: it returns a
    /// <b>composable</b> query, and that query's result type has to be materializable —
    /// <c>string</c> and <c>decimal</c> are not contracts. This is the terminal.
    /// </para>
    /// <para>
    /// It preserves duplicates, unlike <see cref="DistinctValuesAsync{TValue}"/>. The choice
    /// between the two is about what is wanted: an option list calls for the distinct one; a column
    /// of values to sum or plot on the client does not.
    /// </para>
    /// </remarks>
    public Task<List<TValue>> ValuesAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) =>
        ValuesAsync(selector, distinct: false, cancellationToken);

    /// <remarks>
    /// The two value terminals differ by <b>one stage</b>, so they share the path: the single-column
    /// projection is already what both need, and the deduplication is optional.
    /// </remarks>
    private async Task<List<TValue>> ValuesAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        bool distinct,
        CancellationToken cancellationToken)
    {
        DaxPipeline values = DaxStageTranslator.SelectValue(_pipeline, selector.Body, _localizer);

        if (distinct)
            values = DaxStageTranslator.Distinct(values);

        string query = DaxPipelineBuilder.Build(values, _localizer);

        List<DaxValueRow<TValue>> rows =
            await _executor.ExecuteAsync<DaxValueRow<TValue>>(query, cancellationToken);

        return [.. rows.Select(row => row.Value)];
    }

    /// <summary>Every row, as an array.</summary>
    /// <remarks>
    /// It costs one copy over the <see cref="List{T}"/> the executor returns. The executor's
    /// contract is <c>Task&lt;List&lt;T&gt;&gt;</c>; returning the array without copying would
    /// require changing that contract, which is left for later, alongside
    /// <c>IAsyncEnumerable</c>.
    /// </remarks>
    public async Task<T[]> ToArrayAsync(CancellationToken cancellationToken = default) =>
        [.. await ToListAsync(cancellationToken)];

    /// <summary>Every row, indexed by the key.</summary>
    /// <remarks>
    /// The selector is a <see cref="Func{T, TResult}"/>, not an <see cref="Expression"/>: it runs
    /// <b>on the client</b>, over the already-materialized rows, and is not translated to DAX. A
    /// repeated key throws, as in LINQ's <c>ToDictionary</c>.
    /// </remarks>
    public async Task<Dictionary<TKey, T>> ToDictionaryAsync<TKey>(
        Func<T, TKey> keySelector,
        CancellationToken cancellationToken = default) where TKey : notnull =>
        (await ToListAsync(cancellationToken)).ToDictionary(keySelector);

    /// <summary>Every row, indexed by the key and projected by the value selector.</summary>
    /// <remarks>The same note as <see cref="ToDictionaryAsync{TKey}"/> applies: both selectors run on the client.</remarks>
    public async Task<Dictionary<TKey, TValue>> ToDictionaryAsync<TKey, TValue>(
        Func<T, TKey> keySelector,
        Func<T, TValue> valueSelector,
        CancellationToken cancellationToken = default) where TKey : notnull =>
        (await ToListAsync(cancellationToken)).ToDictionary(keySelector, valueSelector);

    /// <summary>
    /// The single row of the result; throws when there are zero or more than one. It asks the
    /// server for <c>TOPN(2)</c> — enough to detect a second row without reading the table.
    /// </summary>
    public async Task<T> SingleAsync(CancellationToken cancellationToken = default)
    {
        List<T> rows = await AtMostTwoAsync(cancellationToken);

        return rows.Count switch
        {
            0 => throw new InvalidOperationException(_localizer.Get("SequenceEmpty")),
            1 => rows[0],
            _ => throw new InvalidOperationException(_localizer.Get("SequenceNotSingle"))
        };
    }

    /// <summary>
    /// The single row of the result, or <see langword="null"/> when there is none. It still throws
    /// when there is more than one — that is the difference between <c>SingleOrDefault</c> and
    /// <c>FirstOrDefault</c>.
    /// </summary>
    public async Task<T?> SingleOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        List<T> rows = await AtMostTwoAsync(cancellationToken);

        return rows.Count switch
        {
            0 => default,
            1 => rows[0],
            _ => throw new InvalidOperationException(_localizer.Get("SequenceNotSingle"))
        };
    }

    /// <remarks>
    /// <para>
    /// The <c>Take(2)</c> is one more stage, not the overwriting of an existing window. With that,
    /// the case that required special handling resolves itself: after a <c>Take(1)</c> the DAX
    /// becomes <c>TOPN(2, TOPN(1, ...))</c>, the inner one returns a single row and
    /// <c>SingleAsync</c> has no reason to throw.
    /// </para>
    /// <para>
    /// Over the flat definition this needed a <c>TopN is 1 ? 1 : 2</c>: overwriting with 2 would
    /// make the query read — and fail over — a row that <c>Take(1).Single()</c> would never see in
    /// LINQ. The guard went away because the structure stopped needing it.
    /// </para>
    /// </remarks>
    private Task<List<T>> AtMostTwoAsync(CancellationToken cancellationToken)
    {
        string query = DaxPipelineBuilder.Build(_pipeline.Then(new DaxTakeStage(2)), _localizer);
        return _executor.ExecuteAsync<T>(query, cancellationToken);
    }

    /// <summary>Counts the rows on the server — <c>COUNTROWS</c>, without fetching them.</summary>
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        string query = DaxPipelineBuilder.BuildCount(_pipeline, _localizer);
        return await _executor.ExecuteCountAsync(query, cancellationToken);
    }

    /// <summary>Counts the rows on the server, returning a <see cref="long"/>.</summary>
    /// <remarks>
    /// The same DAX as <see cref="CountAsync"/>; what changes is the type of the read. A Power BI
    /// fact table passes 2 billion rows without effort, and there <see cref="CountAsync"/> has no
    /// right answer to give.
    /// </remarks>
    public async Task<long> LongCountAsync(CancellationToken cancellationToken = default)
    {
        string query = DaxPipelineBuilder.BuildCount(_pipeline, _localizer);
        object? raw = await _executor.ExecuteScalarAsync(query, cancellationToken);

        // COUNTROWS of an empty table is BLANK, not 0 — and here zero is the right answer.
        return raw is null
            ? 0L
            : (long)DaxValueConverter.Convert(raw, typeof(long), nameof(LongCountAsync), ValueColumn, _localizer);
    }

    /// <summary>
    /// Sums the column on the server — <c>SUMX</c> over the filtered source. An empty result
    /// returns <c>0</c> for a non-nullable <typeparamref name="TValue"/> and <see langword="null"/>
    /// for the nullable form, like <see cref="Enumerable.Sum(IEnumerable{decimal})"/> and its
    /// nullable overload.
    /// </summary>
    public Task<TValue> SumAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) =>
        ScalarAsync<TValue>("SUMX", nameof(SumAsync), selector.Body, emptyIsZero: true, cancellationToken);

    /// <summary>
    /// Smallest value of the column on the server — <c>MINX</c> over the filtered source. An empty
    /// result returns <see langword="null"/> when <typeparamref name="TValue"/> accepts null and
    /// throws an <see cref="InvalidOperationException"/> when it does not, like LINQ's <c>Min</c>.
    /// </summary>
    public Task<TValue> MinAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) =>
        ScalarAsync<TValue>("MINX", nameof(MinAsync), selector.Body, emptyIsZero: false, cancellationToken);

    /// <summary>
    /// Largest value of the column on the server — <c>MAXX</c> over the filtered source. The same
    /// empty semantics as <see cref="MinAsync{TValue}"/>.
    /// </summary>
    public Task<TValue> MaxAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) =>
        ScalarAsync<TValue>("MAXX", nameof(MaxAsync), selector.Body, emptyIsZero: false, cancellationToken);

    /// <summary>
    /// Average of the column on the server — <c>AVERAGEX</c> over the filtered source. It throws an
    /// <see cref="InvalidOperationException"/> when there is no row.
    /// </summary>
    /// <remarks>
    /// <b>It returns a <see cref="double"/>, not a <typeparamref name="TValue"/>.</b> The average of
    /// an integer column is not an integer: typing the return as the column would make
    /// <c>AverageAsync(x =&gt; x.Quantidade)</c> truncate 2.5 to 2 — a wrong number, with no error.
    /// It is the same reason LINQ's <c>Average</c> over an <c>IEnumerable&lt;int&gt;</c> returns a
    /// <see cref="double"/>.
    /// </remarks>
    public async Task<double> AverageAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) =>
        await AverageOrDefaultAsync(selector, cancellationToken)
            ?? throw new InvalidOperationException(_localizer.Get("SequenceEmpty"));

    /// <summary>
    /// Average of the column on the server, returning <see langword="null"/> when there is no row
    /// instead of throwing.
    /// </summary>
    /// <remarks>
    /// It exists because the nullability of LINQ's <c>Average</c> result comes from the selector's
    /// type, and here the return is always a <see cref="double"/> — see
    /// <see cref="AverageAsync{TValue}"/>. So the choice between throwing and returning null needs
    /// a method of its own, the way <see cref="FirstOrDefaultAsync"/> is to
    /// <see cref="FirstAsync"/>.
    /// </remarks>
    public Task<double?> AverageOrDefaultAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) =>
        ScalarAsync<double?>("AVERAGEX", nameof(AverageAsync), selector.Body, emptyIsZero: false, cancellationToken);

    /// <summary>Column name of the scalar <c>ROW</c>, for the conversion error message.</summary>
    private const string ValueColumn = "[Value]";

    /// <param name="iterator">The DAX iterating function — <c>SUMX</c>, <c>MINX</c>, ...</param>
    /// <param name="operator">The LINQ operator's name, for the error message.</param>
    /// <param name="body">The selector's body, still as an expression tree.</param>
    /// <param name="emptyIsZero">
    /// <see langword="true"/> for <c>Sum</c>, whose neutral element is zero; <see langword="false"/>
    /// for <c>Min</c>, <c>Max</c> and <c>Average</c>, which have no value to give over no rows.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// <b>BLANK is the information, not a nuisance.</b> Every DAX iterating aggregate returns
    /// <c>BLANK</c> over an empty table — including <c>SUMX</c>, which does <b>not</b> return zero.
    /// That is why <see cref="IDaxQueryExecutor.ExecuteScalarAsync"/> hands back
    /// <see langword="null"/> instead of flattening to the default: it is that
    /// <see langword="null"/> that distinguishes "summed to zero" from "there was no row", and
    /// therefore decides between returning zero, returning null and throwing.
    /// </para>
    /// <para>
    /// The conversion reuses <c>DaxValueConverter</c>, the same path as row materialization, so the
    /// scalar does not get a type table of its own to diverge.
    /// </para>
    /// </remarks>
    private async Task<TValue> ScalarAsync<TValue>(
        string iterator,
        string @operator,
        Expression body,
        bool emptyIsZero,
        CancellationToken cancellationToken)
    {
        DaxStageTranslator.EnsureNotReshaped(_pipeline, @operator, _localizer);

        IDaxExpression value = new DaxExpressionVisitor(_pipeline.TableName, _localizer)
            .Translate(DaxStageTranslator.Unwrap(body));

        string query = DaxPipelineBuilder.BuildScalar(_pipeline, iterator, value, _localizer);
        object? raw = await _executor.ExecuteScalarAsync(query, cancellationToken);

        if (raw is null)
        {
            return emptyIsZero || AcceptsNull(typeof(TValue))
                ? default!
                : throw new InvalidOperationException(_localizer.Get("SequenceEmpty"));
        }

        Type target = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);

        return (TValue)DaxValueConverter.Convert(
            raw, target, $"{@operator}<{typeof(TValue).Name}>", ValueColumn, _localizer);
    }

    /// <summary>
    /// Whether the type accepts <see langword="null"/> — a reference or a <see cref="Nullable{T}"/>.
    /// It is what separates "returns null over empty" from "throws over empty", as in LINQ.
    /// </summary>
    private static bool AcceptsNull(Type type) =>
        !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    /// <summary>Checks on the server whether any row exists.</summary>
    public async Task<bool> AnyAsync(CancellationToken cancellationToken = default) =>
        await CountAsync(cancellationToken) > 0;

    /// <summary>Checks on the server whether any row satisfies the predicate.</summary>
    /// <remarks>
    /// It composes a <see cref="Where"/> internally. There used to be a window guard here whose
    /// only reason was the <b>message</b>: the inner <c>Where</c> would refuse anyway, but naming
    /// an operator the caller never wrote. With a <c>Where</c> after a window now translated, the
    /// guard lost its object — asking whether any of the first five rows satisfies the predicate is
    /// a well-formed question.
    /// </remarks>
    public Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        Where(predicate).AnyAsync(cancellationToken);

    /// <summary>
    /// Checks whether every row satisfies the predicate, by counting on the server the ones that
    /// <b>do not</b> — <c>All</c> is the negation of <c>Any</c> of the negated predicate.
    /// </summary>
    public async Task<bool> AllAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        await Where(Expression.Lambda<Func<T, bool>>(
            Expression.Not(predicate.Body), predicate.Parameters)).CountAsync(cancellationToken) == 0;

    /// <remarks>
    /// No window guard: ordering after a <c>Take</c> became representable, and it orders the
    /// window's result — which is what LINQ means. See <c>DaxPipelineBuilder</c> for how the pending
    /// terms are consumed.
    /// </remarks>
    private DaxQuery<T> AddOrderBy(Expression body, bool ascending, bool resetsOrder) =>
        new(_executor,
            DaxStageTranslator.OrderBy(_pipeline, body, ascending, resetsOrder, _localizer),
            _localizer);
}
