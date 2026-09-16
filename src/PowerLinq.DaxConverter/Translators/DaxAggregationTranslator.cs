using System.Linq.Expressions;
using System.Reflection;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Builders;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Mapping;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Translators;

/// <summary>
/// Translates the key selector and the result selector of a <see cref="DaxGroupedQuery{T,TKey}"/>
/// into the grouping and extension columns of a <c>SUMMARIZECOLUMNS</c>.
/// </summary>
internal static class DaxAggregationTranslator
{
    /// <summary>Grouping key: one column, or several in <c>new { a, b }</c>.</summary>
    /// <param name="keyBody">The key selector's body; <c>new { a, b }</c> for a composite key.</param>
    /// <param name="tableName">The table the columns are resolved against.</param>
    /// <param name="localizer">Language of the error messages.</param>
    /// <param name="messageKey">
    /// The message key used when a component does not resolve to a column. The default talks about
    /// <c>GroupBy</c>; <c>Join</c> passes its own, otherwise the message would quote an operator the
    /// caller never wrote.
    /// </param>
    /// <param name="resultColumns">
    /// The closed set of result columns, when the query has already been through a reshape;
    /// <see langword="null"/> while the shape is the table's.
    /// </param>
    /// <param name="rowContext">
    /// <see langword="false"/> for the key of a <c>SUMMARIZECOLUMNS</c>, which does <b>not</b> open
    /// a row context: there a navigation comes out as a bare qualified reference, and
    /// <c>RELATED</c> would be a DAX error. <c>Join</c> keeps the default, because its key is
    /// evaluated inside a <c>SELECTCOLUMNS</c>.
    /// </param>
    public static List<DaxColumnRef> TranslateKey(
        Expression keyBody,
        string tableName,
        IPowerLinqLocalizer localizer,
        string messageKey = "GroupByKeyColumnRequired",
        IReadOnlyList<string>? resultColumns = null,
        bool rowContext = true)
    {
        Expression body = Unwrap(keyBody);

        return body is NewExpression composite
            ? [.. composite.Arguments.Select(
                argument => ResolveColumn(argument, tableName, localizer, messageKey, resultColumns, rowContext))]
            : [ResolveColumn(body, tableName, localizer, messageKey, resultColumns, rowContext)];
    }

    /// <summary>
    /// Extension columns from <c>g =&gt; new Result { ... }</c>. The assignment of
    /// <see cref="IDaxGroup{T,TKey}.Key"/> is skipped here — it already goes in as a grouping
    /// column.
    /// </summary>
    public static List<DaxAggregateColumn> TranslateProjection(
        LambdaExpression resultSelector, string tableName, IPowerLinqLocalizer localizer)
    {
        if (resultSelector.Body is not MemberInitExpression init)
            throw new NotSupportedException(localizer.Get("AggregationSelectorInvalid"));

        ParameterExpression group = resultSelector.Parameters[0];
        var columns = new List<DaxAggregateColumn>();

        foreach (MemberBinding binding in init.Bindings)
        {
            if (binding is not MemberAssignment assignment)
                throw new NotSupportedException(localizer.Get("AggregationSelectorPropertyOnly"));

            Expression value = Unwrap(assignment.Expression);
            if (IsKeyAccess(value, group))
                continue;

            columns.Add(new DaxAggregateColumn(
                ExtensionName(assignment.Member),
                TranslateAggregate(value, tableName, localizer)));
        }

        return columns;
    }

    private static bool IsKeyAccess(Expression expression, ParameterExpression group) =>
        expression is MemberExpression member
        && member.Expression == group
        && member.Member.Name == "Key";

    /// <summary>
    /// Name of the extension column: the <see cref="DaxColumnAttribute"/>'s when there is one,
    /// otherwise the property's own name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attribute used to be <b>mandatory</b>, and in the exact <c>[name]</c> form — without it
    /// the aggregation threw. That contradicted the rest of the library: the README states the
    /// attributes are optional, and <c>DaxQuery.OutputName</c> already fell back to
    /// <c>property.Name</c>. Two neighbouring APIs with opposite conventions, and the aggregation's
    /// was the one that forced the issue — a ten-property DTO came out decorated one by one, each
    /// name repeating the property's.
    /// </para>
    /// <para>
    /// Materialization keeps matching with no change: <c>EntityMapper.GetColumnMappings</c> already
    /// registers the <c>[Name]</c> key for a property with no attribute.
    /// </para>
    /// </remarks>
    private static string ExtensionName(MemberInfo member)
    {
        string? reference = member.GetCustomAttribute<DaxColumnAttribute>()?.ColumnReference;

        if (reference is null)
            return member.Name;

        // The extension column's attribute comes in the [name] form; any other form is used as it
        // is, so as not to reinterpret what the user wrote.
        return reference.Length >= 2 && reference[0] == '[' && reference[^1] == ']'
            ? reference[1..^1]
            : reference;
    }

    /// <summary>
    /// The measure's name, evaluated during composition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A literal and a captured variable both count — both are known when the query is composed,
    /// and accepting only the literal would refuse the common case of the measure coming from
    /// configuration. It is the same rule <c>DaxExpressionVisitor</c> applies to closed calls:
    /// whatever does not depend on the parameter is evaluated here and becomes text.
    /// </para>
    /// <para>
    /// A name that <b>depends on the group</b> is refused. It would have to be resolved per row,
    /// and there is no way in DAX to reference a measure whose name is only known during
    /// evaluation.
    /// </para>
    /// </remarks>
    private static string MeasureName(Expression argument, IPowerLinqLocalizer localizer)
    {
        Expression node = Unwrap(argument);

        if (DependsOnGroup(node))
            throw new NotSupportedException(localizer.Get("MeasureNameMustBeConstant"));

        object? value = node is ConstantExpression constant
            ? constant.Value
            : Expression.Lambda(node).Compile().DynamicInvoke();

        return value as string
               ?? throw new NotSupportedException(localizer.Get("MeasureNameMustBeConstant"));
    }

    /// <summary>
    /// The filter-context modifier the call asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MeasureIgnoring</c> with no column becomes <c>REMOVEFILTERS(Table)</c>, not
    /// <c>REMOVEFILTERS()</c>: the latter removes the filter from <b>everything</b>, including
    /// tables the query never mentions, and that is far too powerful to come out of an omitted
    /// argument.
    /// </para>
    /// <para>
    /// <c>MeasureKeepingOnly</c> with no column is refused. It would equal the other one, and the
    /// DAX would come out as a one-argument <c>ALLEXCEPT</c> — which makes the reader wonder what
    /// was excepted.
    /// </para>
    /// </remarks>
    private static IDaxTableExpression Modifier(
        MethodCallExpression call,
        string tableName,
        IPowerLinqLocalizer localizer)
    {
        List<string> columns = call.Arguments.Count > 1
            ? DaxFilterModifierTranslator.Columns(call.Arguments[1], tableName, localizer)
            : [];

        if (call.Method.Name == "MeasureIgnoring")
        {
            return new DaxRemoveFilters(
                columns.Count == 0 ? [DaxIdentifier.Quote(tableName)] : columns);
        }

        if (columns.Count == 0)
            throw new NotSupportedException(localizer.Get("KeepFiltersNeedsAColumn"));

        return new DaxAllExcept(DaxIdentifier.Quote(tableName), columns);
    }

    private static bool DependsOnGroup(Expression node)
    {
        var finder = new GroupReferenceFinder();
        finder.Visit(node);

        return finder.Found;
    }

    private static IDaxExpression TranslateAggregate(
        Expression expression, string tableName, IPowerLinqLocalizer localizer)
    {
        Expression node = Unwrap(expression);

        return node switch
        {
            MethodCallExpression call => TranslateCall(call, tableName, localizer),

            BinaryExpression binary when Arithmetic(binary.NodeType) is { } op =>
                new DaxBinary(op,
                    TranslateAggregate(binary.Left, tableName, localizer),
                    TranslateAggregate(binary.Right, tableName, localizer)),

            ConditionalExpression conditional => new DaxFunctionCall(
                "IF",
                [
                    new DaxExpressionVisitor(tableName, localizer).Translate(conditional.Test),
                    TranslateAggregate(conditional.IfTrue, tableName, localizer),
                    TranslateAggregate(conditional.IfFalse, tableName, localizer)
                ]),

            ConstantExpression constant => DaxLiteral.From(constant.Value),

            _ => throw new NotSupportedException(
                localizer.Format("AggregationExpressionUnsupported", node.NodeType))
        };
    }

    private static IDaxExpression TranslateCall(
        MethodCallExpression call, string tableName, IPowerLinqLocalizer localizer)
    {
        var table = new DaxTableRef(tableName);

        if (call.Method.Name == "Count")
        {
            // Count<TOther>() counts another table of the model. It is the existence gate:
            // SUMMARIZECOLUMNS drops the group when every extension column comes back BLANK, so
            // COUNTROWS of the fact is what prunes the dimension down to "only what has data".
            // Pointing at the query's table when the fact was meant does not return a wrong number
            // — it returns the WHOLE dimension, because the pruning stops happening.
            return call.Method.IsGenericMethod
                ? new DaxFunctionCall(
                    "COUNTROWS",
                    [new DaxTableRef(EntityMapper.GetTableName(call.Method.GetGenericArguments()[0]))])
                : new DaxFunctionCall("COUNTROWS", [table]);
        }

        // A model measure: it goes in as [Measure], with no iterator around it. It ALREADY is the
        // aggregation, and a SUMX around it would add it up once per row of the group — valid DAX,
        // wrong number. The name goes through the same point MeasureAsync uses, and that is
        // mandatory: two ways to reference a measure, a single qualification rule.
        if (call.Method.Name == "Measure")
            return DaxMeasureName.Resolve(MeasureName(call.Arguments[0], localizer));

        // The same measure, evaluated with additional filters that apply only to this column of the
        // projection. The filter enters the CALCULATE alongside the group's context, without
        // affecting the SUMMARIZECOLUMNS' other extensions.
        if (call.Method.Name == "MeasureWhere")
        {
            return DaxPipelineBuilder.MeasureValue(
                MeasureName(call.Arguments[0], localizer),
                AdditionalFilters(call, 1, tableName, localizer),
                []);
        }

        // The same measure, evaluated with the filter context CHANGED. Both modifiers REMOVE
        // filter — what applies it is the query's Where — and that is why the second argument is a
        // column selector and not a predicate: inside CALCULATE the two forms occupy the same
        // position and do opposite things.
        if (call.Method.Name is "MeasureIgnoring" or "MeasureKeepingOnly")
        {
            return DaxPipelineBuilder.MeasureValue(
                MeasureName(call.Arguments[0], localizer),
                [],
                [Modifier(call, tableName, localizer)]);
        }

        // DISTINCTCOUNT only accepts a column, not an expression — there is no DISTINCTCOUNTX. A
        // computed expression here would have to become SUMX over VALUES, which changes the
        // semantics, so it is refused with a message of its own instead of translated wrongly.
        if (call.Method.Name == "CountDistinct")
        {
            IDaxExpression target = new DaxExpressionVisitor(tableName, localizer)
                .Translate(((LambdaExpression)Unwrap(call.Arguments[0])).Body);

            return target is DaxColumnRef distinctColumn
                ? new DaxFunctionCall("DISTINCTCOUNT", [distinctColumn])
                : throw new NotSupportedException(localizer.Get("CountDistinctRequiresColumn"));
        }

        bool filtered = call.Method.Name == "SumWhere";

        (string? scalar, string? iterator) = call.Method.Name switch
        {
            "Sum" or "SumWhere" => ("SUM", "SUMX"),
            "Average" => ("AVERAGE", "AVERAGEX"),
            "Min" => ("MIN", "MINX"),
            "Max" => ("MAX", "MAXX"),
            _ => throw new NotSupportedException(
                localizer.Format("AggregationMethodUnsupported", call.Method.Name))
        };

        IDaxExpression inner = new DaxExpressionVisitor(tableName, localizer)
            .Translate(((LambdaExpression)Unwrap(call.Arguments[0])).Body);

        // A bare column uses the scalar form (SUM); any other expression uses the iterating form
        // (SUMX) over the table.
        IDaxExpression aggregate = inner is DaxColumnRef column
            ? new DaxFunctionCall(scalar, [column])
            : new DaxFunctionCall(iterator, [table, inner]);

        return filtered
            ? DaxPipelineBuilder.CalculateValue(
                aggregate,
                AdditionalFilters(call, 1, tableName, localizer))
            : aggregate;
    }

    /// <summary>
    /// Extracts the bodies of the predicates that arrived as the API's <c>params</c> array, also
    /// accepting an expression captured by the caller.
    /// </summary>
    private static List<Expression> AdditionalPredicateBodies(
        MethodCallExpression call,
        int firstArgument,
        IPowerLinqLocalizer localizer)
    {
        var bodies = new List<Expression>();

        for (int i = firstArgument; i < call.Arguments.Count; i++)
            AddPredicateArgument(call.Arguments[i], bodies, localizer);

        return bodies;
    }

    private static void AddPredicateArgument(
        Expression argument,
        List<Expression> bodies,
        IPowerLinqLocalizer localizer)
    {
        Expression node = Unwrap(argument);

        if (node is NewArrayExpression array)
        {
            foreach (Expression element in array.Expressions)
                AddSinglePredicate(element, bodies, localizer);

            return;
        }

        AddSinglePredicate(node, bodies, localizer);
    }

    private static void AddSinglePredicate(
        Expression argument,
        List<Expression> bodies,
        IPowerLinqLocalizer localizer)
    {
        Expression node = Unwrap(argument);

        if (node is LambdaExpression lambda)
        {
            bodies.Add(Unwrap(lambda.Body));
            return;
        }

        object? value = EvaluateClosed(node);

        switch (value)
        {
            case LambdaExpression captured:
                bodies.Add(Unwrap(captured.Body));
                return;
            case System.Collections.IEnumerable collection:
                {
                    foreach (object? item in collection)
                    {
                        if (item is not Expression expression)
                            throw new NotSupportedException(localizer.Get("AggregationFilterPredicateRequired"));

                        AddSinglePredicate(expression, bodies, localizer);
                    }

                    return;
                }
            default:
                throw new NotSupportedException(localizer.Get("AggregationFilterPredicateRequired"));
        }
    }

    private static IReadOnlyList<IDaxTableExpression> AdditionalFilters(
        MethodCallExpression call,
        int firstArgument,
        string tableName,
        IPowerLinqLocalizer localizer)
    {
        List<Expression> bodies = AdditionalPredicateBodies(call, firstArgument, localizer);

        return bodies.Count == 0 ? throw new NotSupportedException(localizer.Get("AggregationFilterPredicateRequired")) : DaxStageTranslator.FilterTables(tableName, bodies, localizer);
    }

    private static object? EvaluateClosed(Expression node) =>
        node is ConstantExpression constant
            ? constant.Value
            : Expression.Lambda(node).Compile(preferInterpretation: true).DynamicInvoke();

    private static DaxColumnRef ResolveColumn(
        Expression expression,
        string tableName,
        IPowerLinqLocalizer localizer,
        string messageKey = "GroupByKeyColumnRequired",
        IReadOnlyList<string>? resultColumns = null,
        bool rowContext = true) =>
        new DaxExpressionVisitor(tableName, localizer, resultColumns, rowContext)
            .Translate(Unwrap(expression)) as DaxColumnRef
        ?? throw new NotSupportedException(localizer.Get(messageKey));

    private static DaxOperator? Arithmetic(ExpressionType type) => type switch
    {
        ExpressionType.Add or ExpressionType.AddChecked => DaxOperator.Add,
        ExpressionType.Subtract or ExpressionType.SubtractChecked => DaxOperator.Subtract,
        ExpressionType.Multiply or ExpressionType.MultiplyChecked => DaxOperator.Multiply,
        ExpressionType.Divide => DaxOperator.Divide,
        _ => null
    };

    private static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Quote
            } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }
}
