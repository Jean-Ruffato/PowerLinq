using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;
using PowerLinq.DaxConverter.Translators;

namespace PowerLinq.DaxConverter.Builders;

/// <summary>
/// Builds the DAX tree from a <see cref="DaxPipeline"/>, folding the stages in the order they were
/// applied. It does not manipulate text.
/// </summary>
/// <remarks>
/// <para>
/// The fold carries <b>two</b> accumulators: the table expression built so far, and the
/// <b>pending</b> ordering terms. Ordering does not wrap the table — in DAX it is an argument of
/// <c>TOPN</c>. So an ordering stage only accumulates, and what consumes it is the next window;
/// whatever is left becomes the <c>EVALUATE</c>'s <c>ORDER BY</c> clause.
/// </para>
/// <para>
/// <b><c>Take</c> consumes the pending terms, <c>Skip</c> does not.</b> That is what reproduces
/// LINQ's semantics: after a <c>Take</c>, a new ordering applies to the window's result, not to the
/// table — so the earlier terms no longer hold. <c>Skip</c> stays over the same table, and
/// <c>Skip(10).Take(5)</c> needs the same terms in both windows.
/// </para>
/// </remarks>
public static class DaxPipelineBuilder
{
    /// <summary>The query's DAX, as text.</summary>
    public static string Build(DaxPipeline pipeline, IPowerLinqLocalizer? localizer = null) =>
        BuildSyntax(pipeline, localizer).ToDaxString();

    /// <summary>The count's DAX — <c>ROW("[Count]", COUNTROWS(...))</c> — as text.</summary>
    public static string BuildCount(DaxPipeline pipeline, IPowerLinqLocalizer? localizer = null) =>
        BuildCountSyntax(pipeline, localizer).ToDaxString();

    /// <summary>The DAX of a scalar aggregate — <c>ROW("[Value]", SUMX(...))</c> — as text.</summary>
    public static string BuildScalar(
        DaxPipeline pipeline,
        string iterator,
        IDaxExpression value,
        IPowerLinqLocalizer? localizer = null) =>
        BuildScalarSyntax(pipeline, iterator, value, localizer).ToDaxString();

    /// <summary>The query's tree, before writing.</summary>
    /// <param name="pipeline">The stages, in the order they were applied.</param>
    /// <param name="localizer">Language of the error messages; English when omitted.</param>
    /// <exception cref="InvalidOperationException">
    /// A <see cref="DaxSkipStage"/> appeared with no pending ordering — pagination without order is
    /// not deterministic.
    /// </exception>
    public static DaxEvaluate BuildSyntax(DaxPipeline pipeline, IPowerLinqLocalizer? localizer = null)
    {
        Folded folded = Fold(pipeline, localizer);

        return new DaxEvaluate(folded.Source, folded.Pending, folded.Definitions);
    }

    /// <summary>The count's tree, before writing.</summary>
    /// <param name="pipeline">The stages, in the order they were applied.</param>
    /// <param name="localizer">Language of the error messages; English when omitted.</param>
    /// <remarks>
    /// The pending ordering is discarded, and not by oversight: <c>COUNTROWS</c> returns a number,
    /// and ordering a number means nothing. The <b>window</b> still applies, because
    /// <c>Take(10).CountAsync()</c> must count ten.
    /// </remarks>
    public static DaxEvaluate BuildCountSyntax(
        DaxPipeline pipeline,
        IPowerLinqLocalizer? localizer = null)
    {
        Folded folded = Fold(pipeline, localizer);

        var countRows = new DaxFunctionCall("COUNTROWS", [folded.Source]);

        return new DaxEvaluate(
            new DaxTableFunctionCall("ROW", [CountColumnName, countRows]), [], folded.Definitions);
    }

    /// <summary>The scalar aggregate's tree, before writing.</summary>
    /// <param name="pipeline">The stages, in the order they were applied.</param>
    /// <param name="iterator">The iterating function — <c>SUMX</c>, <c>MINX</c>, <c>MAXX</c>, <c>AVERAGEX</c>.</param>
    /// <param name="value">The expression evaluated per row.</param>
    /// <param name="localizer">Language of the error messages; English when omitted.</param>
    /// <remarks>
    /// The <b>iterating</b> form, not the scalar one: <c>SUM(Produto[Preco])</c> would sum in the
    /// current filter context — the whole model in a query without <c>CALCULATE</c> — ignoring the
    /// filter stages with no error at all.
    /// </remarks>
    public static DaxEvaluate BuildScalarSyntax(
        DaxPipeline pipeline,
        string iterator,
        IDaxExpression value,
        IPowerLinqLocalizer? localizer = null)
    {
        Folded folded = Fold(pipeline, localizer);

        var aggregate = new DaxFunctionCall(iterator, [folded.Source, value]);

        return new DaxEvaluate(
            new DaxTableFunctionCall("ROW", [ValueColumnName, aggregate]), [], folded.Definitions);
    }

    /// <summary>
    /// The tree of the page <b>with the total stamped on</b>, before writing: the page and the
    /// count of the whole set in a single query.
    /// </summary>
    /// <param name="pipeline">The stages, in the order they were applied.</param>
    /// <param name="localizer">Language of the error messages; English when omitted.</param>
    /// <remarks>
    /// <para>
    /// The total is of the set <b>before the window</b> — it is what a paged grid shows in "of N
    /// results". Counting after <c>Skip</c>/<c>Take</c> would give the page's size, which the
    /// caller already knows.
    /// </para>
    /// <para>
    /// The pre-window source is declared in a <c>VAR</c>, and so is the count: without declaring
    /// the count, the <c>COUNTROWS</c> would sit inside the <c>ADDCOLUMNS</c> and be written as an
    /// expression evaluated <b>per row of the page</b>.
    /// </para>
    /// <para>
    /// With no window at all the total is the count of the result itself. The source is still
    /// declared, because then it appears in both places — in the <c>ADDCOLUMNS</c> and in the
    /// <c>COUNTROWS</c>.
    /// </para>
    /// </remarks>
    public static DaxEvaluate BuildPagedSyntax(
        DaxPipeline pipeline,
        IPowerLinqLocalizer? localizer = null)
    {
        Folded folded = Fold(pipeline, localizer, bindWindowSource: true);

        IDaxTableExpression source = folded.Source;
        List<DaxVarDefinition> definitions = folded.Definitions;

        IDaxTableExpression counted = folded.BeforeWindow ?? Bind(source, definitions);

        if (folded.BeforeWindow is null)
            source = counted;

        var total = new DaxVarDefinition(TotalVariable, new DaxFunctionCall("COUNTROWS", [counted]));
        definitions.Add(total);

        var stamped = new DaxTableFunctionCall(
            "ADDCOLUMNS",
            [source, DaxLiteral.From(TotalColumn), new DaxScalarVarRef(TotalVariable)]);

        return new DaxEvaluate(stamped, folded.Pending, definitions);
    }

    /// <summary>
    /// The tree for reading a <b>model measure</b>, before writing:
    /// <c>ROW("[Value]", CALCULATE([Measure], &lt;filters&gt;))</c>.
    /// </summary>
    /// <param name="pipeline">The stages, in the order they were applied.</param>
    /// <param name="measure">The measure's name, with or without brackets.</param>
    /// <param name="localizer">Language of the error messages; English when omitted.</param>
    /// <exception cref="NotSupportedException">
    /// An earlier stage changed the result's shape — a window, a projection, a grouping or a join.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The filters go in as <b>filter context</b>, not as an iterated source. That is the
    /// difference between a measure and a column: a column would need a <c>SUMX</c> over the
    /// filtered table, and a measure already is the aggregation — it only needs to know which slice
    /// to evaluate over.
    /// </para>
    /// <para>
    /// With no filters at all there is no <c>CALCULATE</c>: <c>CALCULATE([Measure])</c> with no
    /// filter argument does nothing beyond adding a function to the text.
    /// </para>
    /// <para>
    /// <b>After a reshape it is refused.</b> A window cannot become a filter-context argument
    /// without changing the meaning, and passing it that way would discard it silently — the
    /// measure would be evaluated over the whole model. It is the same refusal grouping makes.
    /// </para>
    /// </remarks>
    public static DaxEvaluate BuildMeasureSyntax(
        DaxPipeline pipeline,
        string measure,
        IPowerLinqLocalizer? localizer = null) =>
        BuildMeasureSyntax(pipeline, measure, [], localizer);

    /// <summary>
    /// The tree for reading a measure, with filter-context <b>modifiers</b>.
    /// </summary>
    /// <param name="pipeline">The stages, in the order they were applied.</param>
    /// <param name="measure">The measure's name, with or without brackets.</param>
    /// <param name="modifiers">
    /// What <b>removes</b> filter from the context — <c>REMOVEFILTERS</c>, <c>ALLEXCEPT</c>.
    /// </param>
    /// <param name="localizer">Language of the error messages; English when omitted.</param>
    /// <remarks>
    /// <para>
    /// The composed filters and the modifiers go into the <b>same</b> argument list, in
    /// filters-then-modifiers order. The order does not change the result — <c>CALCULATE</c>
    /// evaluates every context argument before applying — but fixing it keeps the generated DAX
    /// stable, and stable DAX is what allows freezing literals in tests.
    /// </para>
    /// <para>
    /// <b>A modifier on its own already calls for <c>CALCULATE</c></b>, even with no composed
    /// filter: removing the filter that comes from outside — from the row context of a
    /// <c>SUMMARIZECOLUMNS</c>, for example — is exactly the share-of-total case.
    /// </para>
    /// </remarks>
    public static DaxEvaluate BuildMeasureSyntax(
        DaxPipeline pipeline,
        string measure,
        IReadOnlyList<IDaxTableExpression> modifiers,
        IPowerLinqLocalizer? localizer = null)
    {
        IPowerLinqLocalizer messages = localizer ?? ResourceManagerPowerLinqLocalizer.English;
        Folded folded = Fold(pipeline, localizer);

        if (folded.Reshaped)
            throw new NotSupportedException(messages.Format("OperatorAfterTake", "Measure"));

        IDaxExpression value = MeasureValue(measure, folded.FilterTables, modifiers);

        return new DaxEvaluate(
            new DaxTableFunctionCall("ROW", [ValueColumnName, value]), [], folded.Definitions);
    }

    /// <summary>
    /// The measure, wrapped in <c>CALCULATE</c> only when there is something that changes the
    /// context.
    /// </summary>
    /// <remarks>
    /// <c>CALCULATE([Measure])</c> with no context argument equals <c>[Measure]</c>, and emitting
    /// the function anyway would add noise to every measure read without filters.
    /// </remarks>
    internal static IDaxExpression MeasureValue(
        string measure,
        IReadOnlyList<IDaxTableExpression> filters,
        IReadOnlyList<IDaxTableExpression> modifiers)
    {
        DaxMeasureRef reference = DaxMeasureName.Resolve(measure);

        return filters.Count == 0 && modifiers.Count == 0
            ? reference
            : new DaxCalculate(reference, [.. filters, .. modifiers]);
    }

    /// <summary>
    /// Wraps an aggregated expression in <c>CALCULATE</c> when there are filters specific to it.
    /// </summary>
    /// <remarks>
    /// An extension column can have a scope of its own inside the same <c>SUMMARIZECOLUMNS</c>. The
    /// expression is still evaluated in the group's context, and the additional filters stay
    /// confined to this column's <c>CALCULATE</c>.
    /// </remarks>
    internal static IDaxExpression CalculateValue(
        IDaxExpression value,
        IReadOnlyList<IDaxTableExpression> filters) =>
        filters.Count == 0
            ? value
            : new DaxCalculate(value, [.. filters]);

    /// <summary>The DAX for reading a measure, as text.</summary>
    public static string BuildMeasure(
        DaxPipeline pipeline,
        string measure,
        IPowerLinqLocalizer? localizer = null) =>
        BuildMeasureSyntax(pipeline, measure, [], localizer).ToDaxString();

    /// <summary>The DAX for reading a measure with modifiers, as text.</summary>
    /// <param name="pipeline">The stages, in the order they were applied.</param>
    /// <param name="measure">The measure's name, with or without brackets.</param>
    /// <param name="modifiers">What removes filter from the context.</param>
    /// <param name="localizer">Language of the error messages; English when omitted.</param>
    public static string BuildMeasure(
        DaxPipeline pipeline,
        string measure,
        IReadOnlyList<IDaxTableExpression> modifiers,
        IPowerLinqLocalizer? localizer = null) =>
        BuildMeasureSyntax(pipeline, measure, modifiers, localizer).ToDaxString();

    /// <summary>The DAX of the page with the total stamped on, as text.</summary>
    public static string BuildPaged(DaxPipeline pipeline, IPowerLinqLocalizer? localizer = null) =>
        BuildPagedSyntax(pipeline, localizer).ToDaxString();

    /// <summary>Name of the column that carries the total on the page.</summary>
    /// <remarks>
    /// The <c>__pl_</c> prefix is the same as the <c>Join</c> aliases': the column is a detail of
    /// the query, not of the domain, and its name must not collide with anything the user projects.
    /// </remarks>
    public const string TotalColumn = "[__pl_total]";

    /// <summary>Name of the <c>VAR</c> that holds the total.</summary>
    private const string TotalVariable = "__pl_total";

    /// <summary>Column name of the count ROW — constant, therefore shared.</summary>
    private static readonly IDaxExpression CountColumnName = DaxLiteral.From("[Count]");

    /// <summary>Column name of the scalar aggregate ROW — constant, therefore shared.</summary>
    private static readonly IDaxExpression ValueColumnName = DaxLiteral.From("[Value]");

    /// <summary>
    /// Folds the stages into a table expression, also returning the ordering terms no window
    /// consumed.
    /// </summary>
    /// <summary>What the fold produces. A record, not a tuple: there are six things.</summary>
    /// <param name="Source">The table expression that was built.</param>
    /// <param name="Pending">The ordering terms no window consumed.</param>
    /// <param name="Definitions">The declarations of the <c>DEFINE</c> block.</param>
    /// <param name="BeforeWindow">The source as it stood before the first window, or null.</param>
    /// <param name="FilterTables">The filters in filter-context argument form.</param>
    /// <param name="Reshaped">Whether some stage changed the result's shape.</param>
    private sealed record Folded(
        IDaxTableExpression Source,
        List<DaxOrderTerm> Pending,
        List<DaxVarDefinition> Definitions,
        IDaxTableExpression? BeforeWindow,
        List<IDaxTableExpression> FilterTables,
        bool Reshaped);

    private static Folded Fold(
        DaxPipeline pipeline,
        IPowerLinqLocalizer? localizer,
        List<DaxVarDefinition>? into = null,
        bool bindWindowSource = false)
    {
        IPowerLinqLocalizer messages = localizer ?? ResourceManagerPowerLinqLocalizer.English;

        IDaxTableExpression source = new DaxTableRef(pipeline.TableName);
        var pending = new List<DaxOrderTerm>();

        // The inner side of a Join folds through here too, and its declarations go into the SAME
        // list: there is a single DEFINE block, and numbering per fold would produce two
        // `__pl_source_0` in the same DAX — one silently overwriting the other.
        List<DaxVarDefinition> definitions = into ?? [];

        // The same filters, in the form SUMMARIZECOLUMNS accepts — see DaxGroupStage for why it
        // needs a list instead of the accumulated table. `reshaped` marks that a stage appeared
        // which the list cannot represent.
        var filterTables = new List<IDaxTableExpression>();
        bool reshaped = false;

        // The columns the result carries, or null while the shape is the table's. The rule for
        // which ones lives in DaxPipeline.ColumnsAfter, and is not repeated here: there used to be
        // two copies of the same thing — one serving composition, the other writing — and they
        // diverged the first time one gained a new case, making `Where` refuse a column the DAX
        // does return.
        IReadOnlyList<string>? available = null;

        // The source as it stood right BEFORE the first window. It is what pagination counts: a
        // page's total is that of the filtered set, not of the rows left after skipping.
        IDaxTableExpression? beforeWindow = null;

        // Whether the grouping asked for a subtotal row. Paginating after that would put the total
        // row in the middle of some arbitrary page — it is not a detail row, and TOPN does not know.
        bool subtotal = false;

        for (int i = 0; i < pipeline.Stages.Count; i++)
        {
            available = DaxPipeline.ColumnsAfter(pipeline.Stages[i], available);

            switch (pipeline.Stages[i])
            {
                case DaxFilterStage:
                    // Consecutive stages become ONE FILTER with the conjunction, not a nested
                    // FILTER: both forms give the same result, but the conjunction is the one the
                    // README documents and the one the engine optimizes as a single predicate.
                    IDaxExpression predicate = ConsumeFilters(pipeline, ref i);
                    source = new DaxFilter(source, predicate);
                    filterTables.Add(new DaxFilter(new DaxTableRef(pipeline.TableName), predicate));
                    break;

                case DaxRelatedFilterStage:
                    // Consecutive stages become ONE CALCULATETABLE with several filter-table
                    // arguments: that is how DAX combines filter context, and one CALCULATETABLE
                    // per stage would nest for no reason.
                    List<IDaxTableExpression> related = ConsumeRelatedFilters(pipeline, ref i);
                    source = new DaxCalculateTable(source, related);
                    filterTables.AddRange(related);
                    break;

                case DaxOrderStage order:
                    // After a reshape the result carries a closed set of columns, and ordering
                    // outside it is DAX the server rejects. DaxQuery already resolves against that
                    // set; the check exists here because the builder is public.
                    EnsureColumnIsInResult(order.Term, available, messages);

                    // OrderBy replaces, ThenBy adds — see DaxOrderStage.ResetsOrder.
                    if (order.ResetsOrder)
                        pending.Clear();

                    pending.Add(order.Term);
                    break;

                case DaxSkipStage skip:
                    EnsureNotSubtotalled(subtotal, "Skip", messages);

                    if (pending.Count == 0)
                        throw new InvalidOperationException(messages.Get("SkipRequiresOrderBy"));

                    // The source appears TWICE here, so it is declared once and referenced by name.
                    // Without this a large SUMMARIZECOLUMNS is written — and evaluated — twice just
                    // to skip N rows.
                    source = Bind(source, definitions);
                    beforeWindow ??= source;

                    source = new DaxTableFunctionCall(
                        "EXCEPT",
                        [source, new DaxTopN(source, skip.Count, [.. pending])]);
                    reshaped = true;
                    break;

                case DaxTakeStage take:
                    EnsureNotSubtotalled(subtotal, "Take", messages);

                    // Take does not repeat the source, so it does not declare one — except when
                    // pagination is going to count the total, and then the same source feeds both
                    // the TOPN and the COUNTROWS.
                    if (bindWindowSource && beforeWindow is null)
                        source = Bind(source, definitions);

                    beforeWindow ??= source;

                    // The pending terms are NOT discarded. TOPN chooses WHICH rows come in, and DAX
                    // does not guarantee the order it returns them in — only the EVALUATE's ORDER
                    // BY clause guarantees that. Discarding here produced the right N rows in an
                    // arbitrary order, which in practice usually comes out sorted: worse than
                    // coming out wrong, because it does not show up in a test.
                    source = new DaxTopN(source, take.Count, [.. pending]);
                    reshaped = true;
                    break;

                case DaxDistinctStage:
                    // Does not touch `available`: DISTINCT changes the rows, not the columns.
                    source = new DaxTableFunctionCall("DISTINCT", [source]);
                    break;

                case DaxProjectStage project:
                    RewritePending(pending, Renames(project), "Select", messages);
                    source = new DaxTableFunctionCall("SELECTCOLUMNS", [source, .. Columns(project)]);
                    reshaped = true;
                    break;

                case DaxJoinStage join:
                    RewritePending(pending, Renames(join), "Join", messages);
                    source = Join(source, join, messages, definitions);
                    reshaped = true;
                    break;

                case DaxGroupStage group:
                    // A window cannot be a filter-table argument without changing the meaning, so
                    // passing it that way would discard it silently. Refusing here is the same
                    // decision DaxQuery.EnsureNotWindowed makes during composition — repeated
                    // because the builder is public and someone may build the pipeline by hand.
                    if (reshaped)
                        throw new NotSupportedException(messages.Format("OperatorAfterTake", "GroupBy"));

                    source = new DaxTableFunctionCall(
                        "SUMMARIZECOLUMNS",
                        [.. GroupingColumns(group), .. filterTables, .. Extensions(group)]);

                    EnsureOrderedByKey(group, pending, messages);


                    subtotal = group.RollupLevels.Count > 0;
                    reshaped = true;
                    break;
            }
        }

        return new Folded(source, pending, definitions, beforeWindow, filterTables, reshaped);
    }

    /// <summary>
    /// Declares the source in the <c>DEFINE</c> block and returns the reference to it, so that
    /// whoever uses it more than once does not write it more than once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table reference is returned as it is. <c>EXCEPT(Produto, TOPN(10, Produto, ...))</c>
    /// repeats no work at all — <c>Produto</c> is the name of a model table, not a subtree to
    /// evaluate — and declaring <c>VAR _source = Produto</c> would only add noise to every
    /// <c>Skip</c> over a bare table.
    /// </para>
    /// <para>
    /// An already-declared source is returned as it is: a <c>Skip</c> followed by a <c>Skip</c>
    /// chains over the previous <c>EXCEPT</c>, and each level gets its own declaration.
    /// </para>
    /// </remarks>
    private static IDaxTableExpression Bind(
        IDaxTableExpression source,
        List<DaxVarDefinition> definitions)
    {
        if (source is DaxTableRef or DaxVarRef)
            return source;

        var reference = new DaxVarRef($"__pl_source_{definitions.Count}");
        definitions.Add(new DaxVarDefinition(reference.Name, source));

        return reference;
    }

    /// <summary>
    /// Rewrites the pending ordering terms onto the columns the result now carries, or refuses when
    /// the reshape left one of them out.
    /// </summary>
    /// <param name="pending">The terms accumulated so far; rewritten in place.</param>
    /// <param name="renames">Source reference to output reference.</param>
    /// <param name="operator">The operator that reshaped, for the message.</param>
    /// <param name="messages">Language of the error messages.</param>
    /// <remarks>
    /// <para>
    /// Without this, an <c>OrderBy</c> before a <c>Select</c> or a <c>Join</c> emitted the source
    /// table's reference in the <c>ORDER BY</c> clause — a column the <c>SELECTCOLUMNS</c> does not
    /// return, and which the server rejects.
    /// </para>
    /// <para>
    /// Refusing instead of adding the column to the projection is deliberate: including a column
    /// the caller did not ask for changes the result in order to be able to sort it. The message
    /// states both ways out — include the column, or order afterwards.
    /// </para>
    /// <para>
    /// <b>An expression is rewritten column by column</b>, not refused wholesale as before. One
    /// reference failing to survive the reshape is enough for the whole term to fall — the same
    /// refusal as before, now applied to what is actually missing rather than to the term's shape.
    /// </para>
    /// </remarks>
    private static void RewritePending(
        List<DaxOrderTerm> pending,
        IReadOnlyDictionary<string, string> renames,
        string @operator,
        IPowerLinqLocalizer messages)
    {
        string carried = renames.Count == 0 ? "-" : string.Join(", ", renames.Keys);

        for (int i = 0; i < pending.Count; i++)
        {
            DaxOrderExpressionRewriter.Result rewrite = DaxOrderExpressionRewriter.Rewrite(
                pending[i].Expression,
                reference => renames.GetValueOrDefault(reference));

            if (rewrite.Expression is null)
            {
                throw new NotSupportedException(rewrite.UnmappedColumn is { } column
                    ? messages.Format("OrderByColumnDroppedByReshape", column, @operator, carried)
                    : messages.Format("OrderByExpressionNotRewritable", rewrite.UnsupportedNode));
            }

            pending[i] = pending[i] with { Expression = rewrite.Expression };
        }
    }

    /// <summary>
    /// The projection's columns that are a direct column reference: only those can receive an
    /// earlier ordering, because only those have an identifiable source column.
    /// </summary>
    private static Dictionary<string, string> Renames(DaxProjectStage project)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (DaxProjectionColumn column in project.Columns)
        {
            if (column.Expression is DaxColumnRef source)
                renames[source.Reference] = $"[{column.Name}]";
        }

        return renames;
    }

    /// <summary>
    /// The columns of the join's <b>outer</b> side. The inner side's do not enter: a pending term
    /// can only reference what existed before the join, and before it the inner side did not exist.
    /// </summary>
    private static Dictionary<string, string> Renames(DaxJoinStage join)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);

        // Only a direct column can receive an earlier ordering: in a computed expression there is
        // no identifiable source column to rewrite.
        foreach (DaxJoinProjection projection in join.Projections.Where(p => p.FromOuter))
        {
            if (projection.Expression is DaxColumnRef source)
                renames[source.Reference] = $"[{projection.OutputName}]";
        }

        return renames;
    }

    /// <summary>
    /// Refuses to order by a column the result does not carry, after a reshape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It does nothing while <paramref name="available"/> is <see langword="null"/>: then the shape
    /// is the table's, and any of its columns will do.
    /// </para>
    /// <para>
    /// The check is the <b>same traversal</b> as the rewrite, with the identity as its map: an
    /// expression is valid when every reference of it is in the result. There used to be two rules
    /// — one rewriting, one validating — and the second refused expressions wholesale while the
    /// first already knew how to walk them.
    /// </para>
    /// </remarks>
    private static void EnsureColumnIsInResult(
        DaxOrderTerm term,
        IReadOnlyList<string>? available,
        IPowerLinqLocalizer messages)
    {
        if (available is null)
            return;

        DaxOrderExpressionRewriter.Result rewrite = DaxOrderExpressionRewriter.Rewrite(
            term.Expression,
            reference => available.Contains(reference, StringComparer.Ordinal) ? reference : null);

        if (rewrite.Expression is not null)
            return;

        throw new NotSupportedException(rewrite.UnmappedColumn is { } column
            ? messages.Format("ColumnNotInResult", column, string.Join(", ", available))
            : messages.Format("OrderByExpressionNotRewritable", rewrite.UnsupportedNode));
    }

    /// <summary>
    /// Refuses ordering terms that are not a grouping key.
    /// </summary>
    /// <remarks>
    /// It is not an implementation limitation: the aggregation collapses each group's rows, so
    /// there is no per-output-row value of that column left to sort by. Ordering by the
    /// <b>aggregated value</b> — by the extension column's alias — is a different case.
    /// </remarks>
    private static void EnsureOrderedByKey(
        DaxGroupStage group,
        List<DaxOrderTerm> pending,
        IPowerLinqLocalizer messages)
    {
        foreach (DaxOrderTerm term in pending)
        {
            if (term.Column is not null && group.Keys.Contains(term.Column))
                continue;

            throw new NotSupportedException(messages.Format(
                "OrderByColumnNotGrouped",
                term.Column?.Reference ?? term.Expression.ToDaxString(),
                group.Keys.Count == 0
                    ? "-"
                    : string.Join(", ", group.Keys.Select(column => column.Reference))));
        }
    }

    /// <summary>Prefix of the outer side's key alias, inside the <c>GENERATE</c>.</summary>
    private const string OuterKeyAlias = "__pl_outer_key";

    /// <summary>Prefix of the inner side's key alias, inside the <c>GENERATE</c>.</summary>
    private const string InnerKeyAlias = "__pl_inner_key";

    /// <summary>
    /// Builds the equijoin: reduces both sides to columns of known name, crosses them with
    /// <c>GENERATE</c>, equates the keys in the <c>FILTER</c> and renames onto the output contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outer side is <paramref name="source"/> — the <b>accumulated</b> source — and not the
    /// bare table. It is the only difference from the earlier form, and it is what allows filtering
    /// before joining: for a table with no earlier stage the two coincide.
    /// </para>
    /// <para>
    /// <c>GENERATE</c> has no join clause, so the aliases are not style: they are the mechanism.
    /// Without reducing both sides to known names, there is no way for the <c>FILTER</c> to compare
    /// the keys.
    /// </para>
    /// </remarks>
    private static IDaxTableExpression Join(
        IDaxTableExpression source,
        DaxJoinStage join,
        IPowerLinqLocalizer messages,
        List<DaxVarDefinition> definitions)
    {
        if (join.OuterKeyReferences.Count != join.InnerKeyReferences.Count
            || join.OuterKeyReferences.Count == 0)
        {
            throw new NotSupportedException(messages.Format(
                "JoinKeyArityMismatch",
                join.OuterKeyReferences.Count,
                join.InnerKeyReferences.Count));
        }

        var outerColumns = new List<IDaxNode>();
        var innerColumns = new List<IDaxNode>();

        // One alias pair per key component. The FILTER compares them all with &&, which is what a
        // composite key means.
        for (int k = 0; k < join.OuterKeyReferences.Count; k++)
        {
            outerColumns.Add(new DaxTextLiteral($"{OuterKeyAlias}_{k}"));
            outerColumns.Add(new DaxColumnRef(join.OuterKeyReferences[k]));

            innerColumns.Add(new DaxTextLiteral($"{InnerKeyAlias}_{k}"));
            innerColumns.Add(new DaxColumnRef(join.InnerKeyReferences[k]));
        }

        var output = new List<(string Name, string Alias)>();
        int index = 0;

        foreach (DaxJoinProjection projection in join.Projections)
        {
            // The index is shared between the two sides and advances in projection order: the alias
            // only has to be unique inside the GENERATE, and numbering per side would make the
            // generated DAX depend on how many columns came from each.
            string alias = $"__pl_{(projection.FromOuter ? "outer" : "inner")}_{index++}";

            List<IDaxNode> columns = projection.FromOuter ? outerColumns : innerColumns;
            columns.Add(new DaxTextLiteral(alias));
            columns.Add(projection.Expression);

            output.Add((projection.OutputName, alias));
        }

        var outerSelect = new DaxTableFunctionCall("SELECTCOLUMNS", [source, .. outerColumns]);

        // The inner side goes through the SAME fold: that is what makes its filter appear in the
        // DAX, instead of the join always starting from the bare table.
        IDaxTableExpression innerSource = Fold(
            new DaxPipeline(join.InnerTableName, typeof(object)) { Stages = join.InnerStages },
            messages,
            definitions).Source;

        var innerSelect = new DaxTableFunctionCall(
            "SELECTCOLUMNS", [innerSource, .. innerColumns]);

        IDaxExpression? keysEqual = null;

        for (int k = 0; k < join.OuterKeyReferences.Count; k++)
        {
            var comparison = new DaxBinary(
                DaxOperator.Equal,
                new DaxColumnRef($"[{OuterKeyAlias}_{k}]"),
                new DaxColumnRef($"[{InnerKeyAlias}_{k}]"));

            keysEqual = And(keysEqual, comparison);
        }

        var joined = new DaxTableFunctionCall(
            "GENERATE", [outerSelect, new DaxFilter(innerSelect, keysEqual!)]);

        var resultColumns = new List<IDaxNode>();

        foreach ((string name, string alias) in output)
        {
            resultColumns.Add(new DaxTextLiteral(name));
            resultColumns.Add(new DaxColumnRef($"[{alias}]"));
        }

        return new DaxTableFunctionCall("SELECTCOLUMNS", [joined, .. resultColumns]);
    }

    /// <summary>
    /// Refuses a window over a result that carries a subtotal row.
    /// </summary>
    /// <remarks>
    /// The total row is not a detail row, and <c>TOPN</c> does not know that: it would enter the
    /// page's count and show up in the middle of it, or vanish depending on the page requested. A
    /// grid would show the footer as if it were another category — the same error the
    /// <c>[is_total]</c> column exists to avoid, reintroduced by pagination.
    /// </remarks>
    private static void EnsureNotSubtotalled(
        bool subtotal,
        string @operator,
        IPowerLinqLocalizer messages)
    {
        if (subtotal)
            throw new NotSupportedException(messages.Format("WindowOverSubtotal", @operator));
    }

    /// <summary>Reference of the column that marks the default level's subtotal row, unnamed.</summary>
    public const string SubtotalColumn = "[is_total]";

    /// <summary>
    /// The default level's name as <c>ROLLUPADDISSUBTOTAL</c> receives it — without brackets. Used
    /// by <c>WithSubtotal()</c> with no arguments, which covers the whole key in a single level.
    /// </summary>
    internal const string DefaultSubtotalFlagName = "is_total";

    /// <summary>
    /// The <c>SUMMARIZECOLUMNS</c> grouping columns — raw, or wrapped in the <c>ROLLUPGROUP</c>(s)
    /// that make up <c>ROLLUPADDISSUBTOTAL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ROLLUPGROUP</c>, not <c>ROLLUP</c>, inside <b>each</b> level: the columns of one level are
    /// treated as a single one and return one total row, which is the case of a tax id plus a
    /// company name — the same grain written in two columns.
    /// </para>
    /// <para>
    /// More than one level — one (<c>ROLLUPGROUP</c>, flag name) pair per <c>WithSubtotal</c> call
    /// — is what expresses a hierarchy: <c>ROLLUPADDISSUBTOTAL</c> accepts several pairs, and their
    /// order in the call is the hierarchy's order. See <see cref="DaxRollupLevel"/> for the
    /// semantics of which flag combinations the engine returns.
    /// </para>
    /// <para>
    /// The total row comes back <b>in the same query</b> as the detail rows, and that is what
    /// matters: summing the rows on the client only coincides for an additive measure. For a
    /// <c>DISTINCTCOUNT</c>, an average or a ratio, the engine's total is computed in the total's
    /// filter context, and summing the rows gives the wrong number — plausible enough to pass
    /// review.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <c>DaxFunctionCall</c> here is a declared approximation: the position accepts an
    /// <see cref="IDaxNode"/>, and a <b>grouping specification</b> is neither scalar nor table —
    /// neither of the tree's two types describes it. What matters in this position is the writing,
    /// which is that of a call.
    /// </remarks>
    private static IEnumerable<IDaxNode> GroupingColumns(DaxGroupStage group)
    {
        if (group.RollupLevels.Count == 0)
            return group.Keys;

        var arguments = new List<IDaxNode>();

        foreach (DaxRollupLevel level in group.RollupLevels)
        {
            arguments.Add(new DaxFunctionCall("ROLLUPGROUP", [.. level.Keys]));
            arguments.Add(new DaxTextLiteral(level.FlagName));
        }

        return [new DaxFunctionCall("ROLLUPADDISSUBTOTAL", arguments)];
    }

    /// <summary>
    /// The <c>SUMMARIZECOLUMNS</c> extension arguments: name and expression, alternating.
    /// </summary>
    private static IEnumerable<IDaxNode> Extensions(DaxGroupStage group)
    {
        foreach (DaxAggregateColumn extension in group.Extensions)
        {
            yield return new DaxTextLiteral(extension.Name);
            yield return extension.Expression;
        }
    }

    /// <summary>
    /// The <c>SELECTCOLUMNS</c> column arguments: name and expression, alternating, in declared
    /// order.
    /// </summary>
    private static IEnumerable<IDaxNode> Columns(DaxProjectStage project)
    {
        foreach (DaxProjectionColumn column in project.Columns)
        {
            yield return new DaxTextLiteral(column.Name);
            yield return column.Expression;
        }
    }

    /// <summary>
    /// Consumes the run of <see cref="DaxFilterStage"/> starting at <paramref name="index"/>,
    /// returning the conjunction of the predicates and leaving the index on the last of them.
    /// </summary>
    private static IDaxExpression ConsumeFilters(DaxPipeline pipeline, ref int index)
    {
        IDaxExpression? predicate = null;

        while (index < pipeline.Stages.Count && pipeline.Stages[index] is DaxFilterStage filter)
        {
            predicate = And(predicate, filter.Predicate);
            index++;
        }

        // The outer loop will increment; the index has to point at the last one consumed.
        index--;

        // The loop is only reached with at least one filter stage, so the predicate exists.
        return predicate!;
    }

    /// <summary>
    /// Consumes the run of <see cref="DaxRelatedFilterStage"/> starting at
    /// <paramref name="index"/>, leaving the index on the last of them.
    /// </summary>
    /// <remarks>
    /// Predicates over the <b>same</b> table are merged into a single <c>FILTER</c>, with the
    /// conjunction, and the tables' order of first appearance is preserved: the generated DAX has
    /// to be deterministic, both for diffs and to serve as a cache key.
    /// </remarks>
    private static List<IDaxTableExpression> ConsumeRelatedFilters(DaxPipeline pipeline, ref int index)
    {
        // A list and a linear scan, rather than a dictionary: table comparison is
        // DaxIdentifier.SameTable — which treats 'A B' and A B as the same — and not string
        // equality, so there is no hash key to use. The list is the size of the number of related
        // tables in the query, which is small.
        var grouped = new List<(string Name, IDaxExpression Predicate)>();

        while (index < pipeline.Stages.Count
            && pipeline.Stages[index] is DaxRelatedFilterStage related)
        {
            int existing = grouped.FindIndex(
                entry => DaxIdentifier.SameTable(entry.Name, related.TableName));

            if (existing >= 0)
            {
                grouped[existing] = grouped[existing] with
                {
                    Predicate = And(grouped[existing].Predicate, related.Predicate)
                };
            }
            else
            {
                // The name goes in AS IT CAME: normalizing it would change the quoting DaxTableRef
                // emits.
                grouped.Add((related.TableName, related.Predicate));
            }

            index++;
        }

        index--;

        return [.. grouped.Select(entry =>
            (IDaxTableExpression)new DaxFilter(new DaxTableRef(entry.Name), entry.Predicate))];
    }

    /// <summary>
    /// Structural conjunction: the parentheses are decided on write, by the operators' precedence.
    /// </summary>
    private static IDaxExpression And(IDaxExpression? left, IDaxExpression right) =>
        left is null ? right : new DaxBinary(DaxOperator.And, left, right);
}
