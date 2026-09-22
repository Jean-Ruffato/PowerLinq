using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Translators;

/// <summary>
/// Translates a C# expression tree into a DAX tree.
/// </summary>
/// <remarks>
/// It does not inherit from <see cref="ExpressionVisitor"/>: that class exists to rewrite
/// expression trees into other expression trees, and using it to produce a different kind of output
/// would require a side channel (the previous version's <c>StringBuilder</c>). The typed recursion
/// returns an <see cref="IDaxExpression"/> directly.
/// </remarks>
public sealed class DaxExpressionVisitor
{
    private readonly string _tableName;
    private readonly IPowerLinqLocalizer _localizer;
    private readonly IReadOnlyList<string>? _resultColumns;

    /// <summary>
    /// Whether the expression is in <b>row context</b> — inside a <c>FILTER</c>, an iterator or a
    /// <c>SELECTCOLUMNS</c>.
    /// </summary>
    /// <remarks>
    /// It decides the shape of a navigation, and nothing else. In row context, another table's
    /// column needs <c>RELATED</c>; as the grouping column of a <c>SUMMARIZECOLUMNS</c> — which
    /// opens no row context — <c>RELATED</c> would be <b>wrong</b>, and the bare qualified
    /// reference is the right form. Same C# expression, two DAX outputs, and the one who knows the
    /// position is whoever constructs the translator.
    /// </remarks>
    private readonly bool _rowContext;

    /// <summary>The tables reached through navigation — see <see cref="NavigatedTables"/>.</summary>
    private readonly HashSet<string> _navigatedTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _referencedTables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Translator for the given table, with messages in English.</summary>
    public DaxExpressionVisitor(string tableName)
        : this(tableName, ResourceManagerPowerLinqLocalizer.English) { }

    internal DaxExpressionVisitor(string tableName, IPowerLinqLocalizer localizer)
        : this(tableName, localizer, resultColumns: null) { }

    /// <param name="tableName">The table the columns are resolved against.</param>
    /// <param name="localizer">Language of the error messages.</param>
    /// <param name="resultColumns">
    /// The <b>closed</b> set of columns the result carries, when the query has already been through
    /// a projection, an aggregation or a join; <see langword="null"/> while the shape is the
    /// table's.
    /// </param>
    /// <remarks>
    /// With <paramref name="resultColumns"/> supplied, resolution stops using the entity's mapping
    /// and matches against that set instead. It is what allows filtering the result of a
    /// <c>SELECTCOLUMNS</c> or a <c>SUMMARIZECOLUMNS</c>: there the columns are <c>[Name]</c>, and
    /// the mapping would emit <c>Table[Name]</c> — a reference the result does not have.
    /// </remarks>
    internal DaxExpressionVisitor(
        string tableName,
        IPowerLinqLocalizer localizer,
        IReadOnlyList<string>? resultColumns)
        : this(tableName, localizer, resultColumns, rowContext: true) { }

    /// <param name="tableName">The table the columns are resolved against.</param>
    /// <param name="localizer">Language of the error messages.</param>
    /// <param name="resultColumns">The closed set of result columns, or null.</param>
    /// <param name="rowContext">
    /// <see langword="false"/> when the expression is headed for a position <b>without</b> row
    /// context — today only the grouping column of a <c>SUMMARIZECOLUMNS</c>. See
    /// <see cref="_rowContext"/>.
    /// </param>
    internal DaxExpressionVisitor(
        string tableName,
        IPowerLinqLocalizer localizer,
        IReadOnlyList<string>? resultColumns,
        bool rowContext)
    {
        _tableName = tableName;
        _localizer = localizer;
        _resultColumns = resultColumns;
        _rowContext = rowContext;
    }

    /// <summary>Translates the C# expression into the equivalent DAX tree.</summary>
    /// <exception cref="NotSupportedException">The expression has no DAX translation.</exception>
    public IDaxExpression Translate(Expression expression) => Visit(expression);

    /// <summary>
    /// The tables that own the columns the translation referenced, unquoted. Empty when the
    /// expression touched no column at all.
    /// </summary>
    /// <remarks>
    /// Whoever builds the filter needs this to choose the shape: <c>FILTER</c> opens a row context
    /// on the iterated table, and there another table's column is a DAX error. Collecting during
    /// translation, rather than by walking the DAX tree afterwards, is exact by construction — a
    /// new node in <c>Syntax</c> cannot be forgotten, which would silently generate invalid DAX.
    /// </remarks>
    internal IReadOnlyCollection<string> ReferencedTables => _referencedTables;

    /// <summary>
    /// The tables reached through <b>navigation</b>, and therefore wrapped in <c>RELATED</c>.
    /// </summary>
    /// <remarks>
    /// <b>Kept apart from <see cref="ReferencedTables"/> because validity in row context is the
    /// opposite.</b> A bare qualified reference to another table inside a <c>FILTER</c> is a DAX
    /// error; the same column wrapped in <c>RELATED</c> is exactly what works there. Counting both
    /// in the same set made the "filter over more than one table" refusal fire on precisely the
    /// case navigation exists to enable — comparing columns of two tables.
    /// </remarks>
    internal IReadOnlyCollection<string> NavigatedTables => _navigatedTables;

    /// <summary>Creates the column reference while recording the owning table.</summary>
    private DaxColumnRef Column(string reference)
    {
        if (DaxIdentifier.TableOf(reference) is { } table)
            _referencedTables.Add(table);

        return new DaxColumnRef(reference);
    }

    private IDaxExpression Visit(Expression node) => node switch
    {
        BinaryExpression binary => VisitBinary(binary),
        ConditionalExpression conditional => new DaxFunctionCall(
            "IF", [Visit(conditional.Test), Visit(conditional.IfTrue), Visit(conditional.IfFalse)]),
        UnaryExpression unary => VisitUnary(unary),
        MethodCallExpression call => VisitMethodCall(call),
        MemberExpression member => VisitMember(member),
        ConstantExpression constant => DaxLiteral.From(constant.Value),

        // Before refusing a node, check whether it is closed. `new DateTime(2024,1,1)` does not
        // depend on the lambda's parameter, so it is a constant and can be evaluated here — before,
        // it had to be extracted into a variable just so the compiler would capture it.
        _ => DependsOnParameter(node)
            ? throw new NotSupportedException(
                _localizer.Format("ExpressionUnsupported", node.NodeType))
            : DaxLiteral.From(Evaluate(node))
    };

    private DaxBinary VisitBinary(BinaryExpression node)
    {
        DaxOperator op = node.NodeType switch
        {
            ExpressionType.Equal => DaxOperator.Equal,
            ExpressionType.NotEqual => DaxOperator.NotEqual,
            ExpressionType.GreaterThan => DaxOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => DaxOperator.GreaterThanOrEqual,
            ExpressionType.LessThan => DaxOperator.LessThan,
            ExpressionType.LessThanOrEqual => DaxOperator.LessThanOrEqual,
            ExpressionType.AndAlso or ExpressionType.And => DaxOperator.And,
            ExpressionType.OrElse or ExpressionType.Or => DaxOperator.Or,
            // Text concatenation is not addition. The compiler represents `a + b` over strings as
            // an Add whose Method is string.Concat — and translating that to `+` emitted DAX's
            // ARITHMETIC operator. For numeric text the result was silently wrong: in DAX,
            // "1" + "2" is 3, not "12".
            ExpressionType.Add or ExpressionType.AddChecked => IsTextConcatenation(node)
                ? DaxOperator.Concatenate
                : DaxOperator.Add,
            ExpressionType.Subtract or ExpressionType.SubtractChecked => DaxOperator.Subtract,
            ExpressionType.Multiply or ExpressionType.MultiplyChecked => DaxOperator.Multiply,
            ExpressionType.Divide => DaxOperator.Divide,
            _ => throw new NotSupportedException(
                _localizer.Format("OperatorUnsupported", node.NodeType))
        };

        return new DaxBinary(op, Visit(node.Left), Visit(node.Right));
    }

    private IDaxExpression VisitUnary(UnaryExpression node) => node.NodeType switch
    {
        ExpressionType.Not => new DaxNot(Visit(node.Operand)),

        // The implicit conversion from DaxParameter<T> to T is the shape `p.Column == parameter`
        // takes in the tree. Recognized BEFORE the conversion is crossed: crossing it would let the
        // operand fall into constant folding, which would evaluate the marker — and the marker
        // throws on purpose, so the symptom would be an exception instead of the parameter.
        ExpressionType.Convert or ExpressionType.ConvertChecked
            when TryTranslateParameter(node.Operand) is { } converted => converted,

        // Implicit conversions (boxing, casts inserted by the compiler) have no DAX equivalent and
        // are crossed over.
        ExpressionType.Convert or ExpressionType.ConvertChecked => Visit(node.Operand),

        _ => throw new NotSupportedException(
            _localizer.Format("UnaryOperatorUnsupported", node.NodeType))
    };

    /// <summary>
    /// Translates a subset of <see cref="string"/>'s methods into the equivalent DAX functions.
    /// Text comparison in DAX is case-insensitive by default (the model's collation), so
    /// <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c> ignore case — unlike .NET's ordinal
    /// default. Captured arguments (a variable, for example) have already become literals in
    /// <see cref="Visit"/>.
    /// </summary>
    private IDaxExpression VisitMethodCall(MethodCallExpression node)
    {
        // A closed call is a constant: `DateTime.Today.AddDays(-30)`, `TimeSpan.FromDays(7)`,
        // `decimal.Parse("1.5")`. Evaluating here avoids demanding an intermediate variable and
        // saves translating methods the server never needs to see. The detection only runs on
        // method calls, not on the hot path of a simple comparison.
        if (!DependsOnParameter(node))
            return DaxLiteral.From(Evaluate(node));

        // Set membership: `list.Contains(x.Column)` becomes `x.Column IN { ... }`. It has to come
        // before the text handling, because `Contains` is the same name in both cases — and before,
        // the text path swallowed the collection, generating DAX that looked for the column's value
        // inside the collection's TYPE NAME and therefore never matched.
        if (TryTranslateSetMembership(node) is { } membership)
            return membership;

        MethodInfo method = node.Method;

        // string statics: IsNullOrWhiteSpace, IsNullOrEmpty, Concat and Format.
        if (node.Object is null && method.DeclaringType == typeof(string))
        {
            // An explicit Concat, and the params form: the same chain of `&` as adding strings.
            if (method.Name == nameof(string.Concat))
                return Concatenate(ConcatOperands(node.Arguments));

            // Interpolation. The compiler cannot use the interpolation handler inside an expression
            // tree, so it falls back to string.Format — and translating the format string would
            // require interpreting alignment and specifiers DAX does not express. It refuses while
            // pointing at the alternative that works.
            if (method.Name == nameof(string.Format))
                throw new NotSupportedException(_localizer.Get("StringInterpolationUnsupported"));

            IDaxExpression value = Visit(node.Arguments[0]);
            return method.Name switch
            {
                nameof(string.IsNullOrWhiteSpace) => IsBlankOrEmpty(value, trim: true),
                nameof(string.IsNullOrEmpty) => IsBlankOrEmpty(value, trim: false),
                _ => throw Unsupported(method)
            };
        }

        // Text to number on the server. `int.Parse("1.5")` over a captured variable already became
        // a literal further up; here is the case that depends on the parameter — `int.Parse(x.Col)`
        // — which used to fall into MethodUnsupported and forced the conversion to happen on the
        // client.
        if (node.Object is null && TryTranslateToNumber(node, method) is { } converted)
            return converted;

        // An instance call over a text expression: col.ToUpper(), col.StartsWith("x"), ...
        if (node.Object is null)
            throw Unsupported(method);

        IDaxExpression target = Visit(node.Object);
        return method.Name switch
        {
            nameof(string.ToUpper) => new DaxFunctionCall("UPPER", [target]),
            nameof(string.ToLower) => new DaxFunctionCall("LOWER", [target]),
            nameof(string.Trim) => new DaxFunctionCall("TRIM", [target]),

            // ToString() over text is the identity — it crosses the operand.
            nameof(string.ToString) when node.Arguments.Count == 0 => target,

            // StartsWith(p): LEFT(col, LEN(p)) = p ; EndsWith(s): RIGHT(col, LEN(s)) = s
            nameof(string.StartsWith) => EdgeMatch("LEFT", target, Visit(node.Arguments[0])),
            nameof(string.EndsWith) => EdgeMatch("RIGHT", target, Visit(node.Arguments[0])),

            nameof(string.Contains) => Contains(target, Visit(node.Arguments[0])),

            // MID is 1-based, Substring is 0-based. Without the adjustment the slice would come out
            // one character off — a silent error, not a failure.
            nameof(string.Substring) when node.Arguments.Count == 1 => new DaxFunctionCall(
                "MID",
                [target, OneBased(Visit(node.Arguments[0])), new DaxFunctionCall("LEN", [target])]),

            nameof(string.Substring) when node.Arguments.Count == 2 => new DaxFunctionCall(
                "MID",
                [target, OneBased(Visit(node.Arguments[0])), Visit(node.Arguments[1])]),

            nameof(string.Replace) when node.Arguments.Count == 2 => new DaxFunctionCall(
                "SUBSTITUTE",
                [target, Visit(node.Arguments[0]), Visit(node.Arguments[1])]),

            // SEARCH is 1-based and returns 0 when it finds nothing; IndexOf is 0-based and returns
            // -1. The same -1 resolves both: 1 becomes 0, and 0 becomes -1.
            nameof(string.IndexOf) when node.Arguments.Count == 1 => new DaxBinary(
                DaxOperator.Subtract,
                new DaxFunctionCall(
                    "SEARCH",
                    [
                        Visit(node.Arguments[0]),
                        target,
                        new DaxNumberLiteral(1),
                        new DaxNumberLiteral(0)
                    ]),
                new DaxNumberLiteral(1)),

            _ => throw Unsupported(method)
        };

    }

    /// <summary>
    /// Translates <c>collection.Contains(column)</c> into <c>column IN { ... }</c>, when the call
    /// has that shape. Returns <c>null</c> when it does not, so the flow can continue to the text
    /// handling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The identification is not by declaring type but by the call's <b>shape</b>: one closed
    /// operand that evaluates to a collection, and one that depends on the lambda's parameter. That
    /// covers <c>List&lt;T&gt;.Contains</c>, <c>HashSet&lt;T&gt;.Contains</c>,
    /// <c>Enumerable.Contains</c> and the <c>MemoryExtensions.Contains</c> over a
    /// <c>ReadOnlySpan</c>, which is where the compiler resolves <c>array.Contains(x)</c>, all at
    /// once — three different signatures for the same intent.
    /// </para>
    /// <para>
    /// <see cref="string"/> is excluded on purpose: it is an <see cref="IEnumerable{T}"/> of
    /// <see cref="char"/>, so it would match here and steal the path of
    /// <c>text.Contains(value)</c>, which must keep becoming <c>SEARCH</c>.
    /// </para>
    /// </remarks>
    private IDaxExpression? TryTranslateSetMembership(MethodCallExpression node)
    {
        if (node.Method.Name != nameof(Enumerable.Contains))
            return null;

        Expression? valueExpression = null;
        System.Collections.IEnumerable? items = null;

        // The receiver and the arguments are all candidates: the collection's position changes
        // between the instance form and the static ones.
        foreach (Expression candidate in Operands(node))
        {
            if (DependsOnParameter(candidate))
            {
                // More than one operand depending on the parameter is not membership in a constant
                // set — leave it for another path to decide.
                if (valueExpression is not null)
                    return null;

                valueExpression = candidate;
            }
            else if (items is null && TryEvaluateCollection(candidate, out System.Collections.IEnumerable? evaluated))
            {
                items = evaluated;
            }
        }

        if (valueExpression is null || items is null)
            return null;

        List<IDaxExpression> literals = [.. items.Cast<object?>().Select(DaxLiteral.From)];

        // `IN { }` is a syntax error in DAX. An empty set matches nothing, so the predicate is a
        // constant false — and negating it becomes NOT(FALSE), that is, TRUE, with no special
        // handling.
        return literals.Count == 0
            ? DaxBooleanLiteral.False
            : new DaxIn(Visit(valueExpression), literals);
    }

    private static IEnumerable<Expression> Operands(MethodCallExpression node)
    {
        if (node.Object is not null)
            yield return node.Object;

        foreach (Expression argument in node.Arguments)
            yield return argument;
    }

    /// <summary>
    /// Evaluates a closed operand that represents a collection of values.
    /// </summary>
    /// <remarks>
    /// The conversions are crossed before evaluating because <c>array.Contains(x)</c> arrives as
    /// <c>MemoryExtensions.Contains</c> over a <c>ReadOnlySpan&lt;T&gt;</c> converted from the
    /// array. Evaluating the conversion would produce a span, which cannot be boxed — crossing down
    /// to the original array avoids that.
    /// </remarks>
    private bool TryEvaluateCollection(
        Expression expression,
        out System.Collections.IEnumerable? items)
    {
        items = null;
        Expression unwrapped = UnwrapConversions(expression);

        // string is IEnumerable<char>: excluded so it does not steal the text path.
        if (unwrapped.Type == typeof(string) || !typeof(System.Collections.IEnumerable).IsAssignableFrom(unwrapped.Type))
            return false;

        object? value = Evaluate(unwrapped);

        items = (IEnumerable)value! ?? throw new NotSupportedException(_localizer.Get("SetMembershipCollectionNull"));
        return true;
    }

    private static Expression UnwrapConversions(Expression expression)
    {
        while (true)
        {
            switch (expression)
            {
                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                    expression = convert.Operand;
                    continue;

                // An implicit conversion operator, such as int[] -> ReadOnlySpan<int>.
                case MethodCallExpression { Object: null, Arguments.Count: 1 } call
                    when call.Method.Name is "op_Implicit" or "op_Explicit":
                    expression = call.Arguments[0];
                    continue;

                default:
                    return expression;
            }
        }
    }

    /// <summary>
    /// Tells concatenation from addition. The reliable signal is the result's type: an <c>Add</c>
    /// that produces a <see cref="string"/> is concatenation, and the compiler also attaches
    /// <c>string.Concat</c> as the node's method.
    /// </summary>
    private static bool IsTextConcatenation(BinaryExpression node) =>
        node.Type == typeof(string) || node.Method?.DeclaringType == typeof(string);

    /// <summary>Chains the operands with <c>&amp;</c>, associating to the left as C# does.</summary>
    private IDaxExpression Concatenate(IReadOnlyList<Expression> operands)
    {
        if (operands.Count == 0)
            return new DaxTextLiteral(string.Empty);

        IDaxExpression chain = Visit(operands[0]);

        for (int i = 1; i < operands.Count; i++)
            chain = new DaxBinary(DaxOperator.Concatenate, chain, Visit(operands[i]));

        return chain;
    }

    /// <summary>
    /// Operands of <c>string.Concat</c>. The <c>params</c> overload arrives as a single array
    /// argument, so the array is opened up instead of being treated as a value.
    /// </summary>
    private static IReadOnlyList<Expression> ConcatOperands(IReadOnlyList<Expression> arguments) =>
        arguments is [NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array]
            ? array.Expressions
            : arguments;

    // ISBLANK(v) || v = ""  (with TRIM(v) in place of v for IsNullOrWhiteSpace).
    private static IDaxExpression IsBlankOrEmpty(IDaxExpression value, bool trim)
    {
        IDaxExpression left = trim ? new DaxFunctionCall("TRIM", [value]) : value;
        var isEmpty = new DaxBinary(DaxOperator.Equal, left, new DaxTextLiteral(string.Empty));
        var isBlank = new DaxFunctionCall("ISBLANK", [value]);
        return new DaxBinary(DaxOperator.Or, isBlank, isEmpty);
    }

    /// <summary>
    /// Converts C#'s 0-based index to DAX's 1-based one. A literal index — the common case,
    /// <c>Substring(0, 1)</c> — is added during translation, so the DAX comes out as
    /// <c>MID(t, 1, 1)</c> instead of the noisy <c>MID(t, 0 + 1, 1)</c>.
    /// </summary>
    private static IDaxExpression OneBased(IDaxExpression zeroBased) =>
        zeroBased is DaxNumberLiteral { Value: int index }
            ? new DaxNumberLiteral(index + 1)
            : new DaxBinary(DaxOperator.Add, zeroBased, new DaxNumberLiteral(1));

    // side(target, LEN(needle)) = needle — used by StartsWith (LEFT) and EndsWith (RIGHT).
    private static IDaxExpression EdgeMatch(string side, IDaxExpression target, IDaxExpression needle)
    {
        var length = new DaxFunctionCall("LEN", [needle]);
        var slice = new DaxFunctionCall(side, [target, length]);
        return new DaxBinary(DaxOperator.Equal, slice, needle);
    }

    // SEARCH(needle, target, 1, 0) > 0 — the 4th argument 0 returns 0 (instead of an error) when
    // nothing is found.
    private static IDaxExpression Contains(IDaxExpression target, IDaxExpression needle)
    {
        var search = new DaxFunctionCall(
            "SEARCH", [needle, target, new DaxNumberLiteral(1), new DaxNumberLiteral(0)]);
        return new DaxBinary(DaxOperator.GreaterThan, search, new DaxNumberLiteral(0));
    }

    /// <summary>
    /// The numeric types whose <c>Parse</c> and whose <c>Convert.To*</c> become <c>VALUE</c>.
    /// </summary>
    /// <remarks>
    /// In DAX there is no function per type: <c>VALUE</c> returns a number, and the final type is
    /// the column's or the conversion's at materialization time. Distinguishing <c>int</c> from
    /// <c>decimal</c> here would not change the generated DAX.
    /// </remarks>
    private static readonly HashSet<Type> NumericParseTargets =
        [typeof(int), typeof(long), typeof(decimal), typeof(double)];

    private static readonly HashSet<string> ConvertToNumber =
        [nameof(Convert.ToInt32), nameof(Convert.ToInt64),
         nameof(Convert.ToDecimal), nameof(Convert.ToDouble)];

    /// <summary>
    /// Translates a text-to-number conversion — <c>VALUE</c>. Returns <see langword="null"/> when
    /// the call is not one of those, so the flow can continue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The culture diverges, and that cannot be resolved here.</b> <c>VALUE</c> interprets the
    /// text by the <i>model's locale</i>, and <c>int.Parse</c> without an <c>IFormatProvider</c>
    /// uses the process's current culture. <c>"1.234"</c> is one thousand two hundred and
    /// thirty-four in a pt-BR model and just over one in an en-US one. The translator does not know
    /// the model's locale, so promising equivalence would be false — it is documented in the README.
    /// </para>
    /// <para>
    /// That is why the overload <b>with</b> an <c>IFormatProvider</c> is refused: honouring it would
    /// require imposing the culture on the server, and translating it anyway would silently discard
    /// the very argument the author passed in order not to depend on the culture.
    /// </para>
    /// <para>
    /// <c>Convert.To*</c> over a <b>number</b> is refused too: there it rounds — and to the nearest
    /// even, not up — behaviour <c>VALUE</c> does not have and <c>ROUND</c> does not reproduce
    /// either.
    /// </para>
    /// <para>
    /// <c>TryParse</c> has a refusal of <b>its own</b>, not the generic untranslatable-method one:
    /// its reason is not a missing implementation, and the way out is switching to <c>Parse</c> —
    /// see <see cref="EnsureNotTryParse"/>.
    /// </para>
    /// </remarks>
    private IDaxExpression? TryTranslateToNumber(MethodCallExpression node, MethodInfo method)
    {
        EnsureNotTryParse(method);

        bool isParse = method.Name == "Parse"
                       && method.DeclaringType is { } declaring
                       && NumericParseTargets.Contains(declaring);

        bool isConvert = method.DeclaringType == typeof(Convert)
                         && ConvertToNumber.Contains(method.Name);

        if (!isParse && !isConvert)
            return null;

        if (node.Arguments.Count != 1)
        {
            throw new NotSupportedException(_localizer.Format(
                "ParseWithFormatProviderUnsupported", method.DeclaringType?.Name, method.Name));
        }

        if (node.Arguments[0].Type != typeof(string))
        {
            throw new NotSupportedException(_localizer.Format(
                "ConvertFromNumberUnsupported", method.DeclaringType?.Name, method.Name));
        }

        return new DaxFunctionCall("VALUE", [Visit(node.Arguments[0])]);
    }

    /// <summary>
    /// Refuses <c>TryParse</c> of the same types whose <c>Parse</c> is translated, with a message
    /// of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generic untranslatable-method refusal would say "not supported", which here is
    /// misleading: the reason is not a missing implementation, it is <b>two</b> semantic
    /// incompatibilities, and both would still hold with any implementation.
    /// </para>
    /// <para>
    /// The <c>out</c> parameter has nowhere to go: a DAX expression returns <b>one</b> value, and
    /// there is no second output channel. And <c>TryParse</c> promises <b>not to throw</b> — it
    /// returns <see langword="false"/> on text that does not convert — while <c>VALUE</c> raises an
    /// error there. Translating it anyway would swap a <see langword="false"/> for a query that
    /// fails, and the worst case is not the failure: it is the predicate that depended on that
    /// <see langword="false"/> never getting the chance to be evaluated.
    /// </para>
    /// <para>
    /// Only for <see cref="NumericParseTargets"/>, and not for every <c>TryParse</c>: the message
    /// says to use <c>Parse</c>, and that advice is only true where <c>Parse</c> translates.
    /// <c>DateTime.TryParse</c> still falls into the generic refusal, which is the right one for it
    /// — there <c>Parse</c> has no translation either.
    /// </para>
    /// </remarks>
    private void EnsureNotTryParse(MethodInfo method)
    {
        if (method.Name != "TryParse"
            || method.DeclaringType is not { } declaring
            || !NumericParseTargets.Contains(declaring))
        {
            return;
        }

        throw new NotSupportedException(_localizer.Format("TryParseUnsupported", declaring.Name));
    }

    private NotSupportedException Unsupported(MethodInfo method) =>
        new(_localizer.Format(
            "MethodUnsupported", method.DeclaringType?.Name, method.Name));

    private IDaxExpression VisitMember(MemberExpression node)
    {
        // Direct access to a property of the lambda's parameter: p => p.Nome
        if (node.Expression is ParameterExpression && node.Member is PropertyInfo prop)
        {
            return _resultColumns is null
                ? Column(ResolveColumnReference(prop, _tableName))
                : Column(ResolveResultColumn(prop, _resultColumns, _localizer));
        }

        // A member that still depends on the parameter (p.Nome.Length, p.Data.Year, p.Opcional.Value)
        // is neither a constant nor a column: there is no translation. Without this guard the flow
        // fell into EvaluateMember and ended in an Expression.Lambda(node).Compile() over a free
        // parameter, which blows up with "variable 'p' referenced from scope '', but it is not
        // defined" — a runtime message that says neither which member failed nor that the cause is
        // translation.
        if (DependsOnParameter(node))
        {
            // Navigation to another entity: `r.Exporter.CnpjRoot`. It comes before the nested-member
            // translations because `p.Data.Year` is also a member over a member, and there the owner
            // is not a navigation — the recognizer returns null and the flow continues.
            if (DaxNavigation.Resolve(node) is { } navigation)
                return Navigate(navigation);

            // Some nested members have a direct DAX equivalent. The ones that do not are still
            // refused, naming the path.
            return TryTranslateMember(node)
                ?? throw new NotSupportedException(
                    _localizer.Format("MemberNotTranslatable", DescribeMemberPath(node)));
        }

        // `parameter.Value`, the explicit form. It has to come before the folding for the same
        // reason as the implicit conversion — and covering both is not fussiness: whichever one was
        // left out would evaluate the marker, and since the marker throws, the user would see an
        // obscure exception in place of the parameter.
        if (node.Member.Name == nameof(DaxParameter<object>.Value)
            && TryTranslateParameter(node.Expression) is { } fromValue)
        {
            return fromValue;
        }

        return DaxLiteral.From(EvaluateMember(node));
    }

    /// <summary>
    /// Returns the parameter node when <paramref name="expression"/> evaluates to a marker, or
    /// <see langword="null"/> when that is not what it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It evaluates <b>the marker</b>, not its value: the marker is the object the closed expression
    /// returns — normally a closure field — and reading its <c>Slot</c> is safe. What throws is
    /// <c>Value</c>, which is not touched here.
    /// </para>
    /// <para>
    /// An expression that depends on the lambda's parameter is discarded right away: it is not
    /// closed, so evaluating it would blow up — and a marker never comes from there, because it is
    /// captured from outside.
    /// </para>
    /// </remarks>
    private static IDaxExpression? TryTranslateParameter(Expression? expression)
    {
        if (expression is null
            || !typeof(IDaxParameter).IsAssignableFrom(expression.Type)
            || DependsOnParameter(expression))
        {
            return null;
        }

        return Evaluate(expression) is IDaxParameter parameter
            ? new DaxParameterRef(parameter.Slot)
            : null;
    }

    /// <summary>
    /// The navigation in the form the position requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The destination table goes into <see cref="_navigatedTables"/>, and <b>not</b> into
    /// <see cref="_referencedTables"/>: in row context a bare reference to another table is a DAX
    /// error, and the same column wrapped in <c>RELATED</c> is what works. Adding them together
    /// made the "filter over more than one table" refusal fire on the case navigation exists to
    /// enable.
    /// </para>
    /// <para>
    /// Outside row context the destination counts as an ordinary reference — there the navigation
    /// comes out as a qualified reference, which is the form the grouping column of a
    /// <c>SUMMARIZECOLUMNS</c> wants.
    /// </para>
    /// </remarks>
    private IDaxExpression Navigate(DaxNavigationPath navigation)
    {
        string target = navigation.Hops[^1].To;
        var column = new DaxColumnRef(navigation.Column);

        if (!_rowContext)
        {
            _referencedTables.Add(target);
            return column;
        }

        _navigatedTables.Add(target);

        return new DaxRelated(column);
    }

    /// <summary>
    /// Translates a member over something that is already translatable — <c>p.Data.Year</c>,
    /// <c>p.Nome.Length</c> — or returns <c>null</c> when there is no equivalent.
    /// </summary>
    private IDaxExpression? TryTranslateMember(MemberExpression node)
    {
        if (node.Expression is null)
            return null;

        // Nullable<T>.Value is the identity: in DAX a nullable column is the same column. Without
        // this, a date part over a DateTime? would be unreachable, because in C# the only way to
        // get there is through .Value — which is precisely the acceptance criterion for a nullable
        // column.
        if (node.Member.Name == "Value" && Nullable.GetUnderlyingType(node.Expression.Type) is not null)
            return Visit(node.Expression);

        Type ownerType = Nullable.GetUnderlyingType(node.Expression.Type) ?? node.Expression.Type;

        if (ownerType == typeof(string))
        {
            return node.Member.Name == nameof(string.Length)
                ? new DaxFunctionCall("LEN", [Visit(node.Expression)])
                : null;
        }

        return ownerType == typeof(DateTime) || ownerType == typeof(DateTimeOffset) || ownerType == typeof(DateOnly)
            ? TryTranslateDatePart(node)
            : null;
    }

    /// <summary>
    /// Date and time parts. Filtering and grouping by year or month is the central case of any
    /// model with a time dimension, and it used to require a ready-made period column in the model.
    /// </summary>
    private IDaxExpression? TryTranslateDatePart(MemberExpression node)
    {
        string? function = node.Member.Name switch
        {
            nameof(DateTime.Year) => "YEAR",
            nameof(DateTime.Month) => "MONTH",
            nameof(DateTime.Day) => "DAY",
            nameof(DateTime.Hour) => "HOUR",
            nameof(DateTime.Minute) => "MINUTE",
            nameof(DateTime.Second) => "SECOND",
            _ => null
        };

        if (function is not null)
            return new DaxFunctionCall(function, [Visit(node.Expression!)]);

        return node.Member.Name switch
        {
            // DateTime.Date truncates the time. DAX has no equivalent function, so it recomposes the
            // date from its parts.
            nameof(DateTime.Date) => TruncateToDate(Visit(node.Expression!)),

            // WEEKDAY with return type 1 gives 1 for Sunday; .NET's DayOfWeek gives 0. The -1 aligns
            // the two — without it the value would come out one day off, silently.
            nameof(DateTime.DayOfWeek) => new DaxBinary(
                DaxOperator.Subtract,
                new DaxFunctionCall("WEEKDAY", [Visit(node.Expression!), new DaxNumberLiteral(1)]),
                new DaxNumberLiteral(1)),

            _ => null
        };
    }

    private static IDaxExpression TruncateToDate(IDaxExpression value) =>
        new DaxFunctionCall(
            "DATE",
            [
                new DaxFunctionCall("YEAR", [value]),
                new DaxFunctionCall("MONTH", [value]),
                new DaxFunctionCall("DAY", [value])
            ]);

    /// <summary>
    /// Whether the subtree references the lambda's parameter — that is, whether it depends on the
    /// row being evaluated. When it does not, the expression is constant and can be resolved during
    /// translation; when it does, either there is a DAX translation or the case must be refused.
    /// </summary>
    /// <remarks>
    /// Here <see cref="ExpressionVisitor"/> is the right tool, unlike what the class's note
    /// describes for translation: this is only <b>looking for</b> a node, not producing another form
    /// of output. The scan runs only on the fallback paths — never on the simple comparison that
    /// dominates the hot path.
    /// </remarks>
    private static bool DependsOnParameter(Expression expression) =>
        ParameterDetector.Detect(expression);

    /// <summary>Describes the member path as <c>Produto.Nome.Length</c>, for the message.</summary>
    private static string DescribeMemberPath(MemberExpression node)
    {
        var parts = new Stack<string>();
        Expression? current = node;

        while (current is MemberExpression member)
        {
            parts.Push(member.Member.Name);
            current = member.Expression;
        }

        if (current is ParameterExpression parameter)
            parts.Push(parameter.Type.Name);

        return string.Join('.', parts);
    }

    /// <summary>
    /// Evaluates a closed expression. It prefers interpretation over IL emission: this is a single
    /// evaluation, and compiling to IL costs orders of magnitude more than interpreting once.
    /// </summary>
    private static object? Evaluate(Expression node) =>
        node is MemberExpression member
            ? EvaluateMember(member)
            : Expression.Lambda(node).Compile(preferInterpretation: true).DynamicInvoke();

    /// <summary>
    /// The reference declared in <see cref="DaxColumnAttribute"/> per property, or <c>null</c> when
    /// the property has no attribute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dominant cost of translation was not walking the tree, it was
    /// <c>GetCustomAttribute</c>: it <b>materializes a new instance of the attribute</b> on every
    /// property access. Measured, two predicates of identical shape differing only in the attribute
    /// cost 950 ns (with) against 209 ns (without) — those ~741 ns of difference were nothing but
    /// the instantiation, and they grew linearly with the number of terms in the predicate.
    /// </para>
    /// <para>
    /// The cache keeps only the attribute's value, and <b>not</b> the composed reference. That way
    /// the key is the <see cref="PropertyInfo"/> itself, with no need for the table name: the
    /// attribute does not depend on it, and the <c>Table[Property]</c> fallback is built on each
    /// call, which is cheap. Two entities with a property of the same name do not collide, because
    /// each <see cref="PropertyInfo"/> belongs to its own declaring type.
    /// </para>
    /// <para>
    /// The dictionary is static and holds references to the mapped types, which prevents the
    /// assembly that declares them from being unloaded. For a query library that is acceptable —
    /// the set of mapped properties is small and finite. In a plugin scenario with a dynamic
    /// assembly, the alternative would be a <c>ConditionalWeakTable</c>, at the cost of a slower
    /// lookup.
    /// </para>
    /// </remarks>
    private static readonly ConcurrentDictionary<PropertyInfo, string?> DeclaredColumnReferences = new();

    /// <summary>
    /// Resolves a property's DAX reference, honouring <see cref="DaxColumnAttribute"/> and falling
    /// back to <c>Table[Property]</c>.
    /// </summary>
    /// <summary>
    /// The column's reference within the set the result carries.
    /// </summary>
    /// <remarks>
    /// Both candidate forms come from the same place: <c>[DaxColumn("[Total]")]</c> names an
    /// extension column and becomes <c>[Total]</c>; <c>[DaxColumn("Venda[Categoria]")]</c> names a
    /// key column and becomes itself. Testing both is what makes extension and key work through the
    /// same call.
    /// </remarks>
    internal static string ResolveResultColumn(
        PropertyInfo property,
        IReadOnlyList<string> resultColumns,
        IPowerLinqLocalizer localizer)
    {
        string name = OutputName(property);

        foreach (string candidate in (string[])[$"[{name}]", name])
        {
            if (resultColumns.Contains(candidate, StringComparer.Ordinal))
                return candidate;
        }

        throw new NotSupportedException(localizer.Format(
            "ColumnNotInResult",
            $"{property.DeclaringType?.Name}.{property.Name}",
            resultColumns.Count == 0 ? "-" : string.Join(", ", resultColumns)));
    }

    /// <summary>
    /// The column's name in the result: the <c>[DaxColumn]</c> attribute's without the brackets, or
    /// the property's name.
    /// </summary>
    internal static string OutputName(PropertyInfo property)
    {
        string? reference = property.GetCustomAttribute<Attributes.DaxColumnAttribute>()?.ColumnReference;

        if (reference is null)
            return property.Name;

        return reference.StartsWith('[') && reference.EndsWith(']')
            ? reference[1..^1]
            : reference;
    }

    internal static string ResolveColumnReference(PropertyInfo property, string tableName) =>
        DeclaredColumnReferences.GetOrAdd(
            property,
            static declaring => declaring.GetCustomAttribute<DaxColumnAttribute>()?.ColumnReference)
        ?? DaxIdentifier.Column(tableName, property.Name);

    /// <summary>
    /// Extracts the value of an expression that does not depend on the lambda's parameter —
    /// typically a captured variable, which the compiler turns into a field of a closure class.
    /// </summary>
    /// <remarks>
    /// The fast path reads the field or the property by reflection. The fallback compiles a lambda,
    /// which costs orders of magnitude more; measured in
    /// <c>DaxExpressionVisitorBenchmarks.CapturedVariable</c>.
    /// </remarks>
    private static object? EvaluateMember(MemberExpression node)
    {
        object? owner = node.Expression switch
        {
            null => null,                              // a static member
            ConstantExpression constant => constant.Value,
            MemberExpression inner => EvaluateMember(inner),
            _ => Unevaluated
        };

        if (ReferenceEquals(owner, Unevaluated))
            return Expression.Lambda(node).Compile(preferInterpretation: true).DynamicInvoke();
        return node.Member switch
        {
            FieldInfo field => field.GetValue(owner),
            PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(owner),
            _ => Expression.Lambda(node).Compile(preferInterpretation: true).DynamicInvoke()
        };
    }

    /// <summary>Sentinel for "could not be resolved without compiling".</summary>
    private static readonly object Unevaluated = new();
}
