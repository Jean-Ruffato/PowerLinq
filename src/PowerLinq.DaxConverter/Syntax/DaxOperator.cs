namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// A DAX binary operator. Symbol, precedence and associativity are data carried by the operator
/// itself, not a <c>switch</c> consulted on every write.
/// </summary>
/// <remarks>
/// The precedence follows the official DAX table, from strongest to weakest:
/// <c>* /</c> &gt; <c>+ -</c> &gt; <c>&amp;</c> &gt; comparison &gt;
/// <c>&amp;&amp;</c> &gt; <c>||</c>. That is what makes it possible to emit only the parentheses
/// that are needed.
/// </remarks>
public sealed class DaxOperator
{
    private DaxOperator(string symbol, int precedence, bool isAssociative)
    {
        Symbol = symbol;
        Precedence = precedence;
        IsAssociative = isAssociative;
    }

    /// <summary>The symbol as DAX writes it.</summary>
    public string Symbol { get; }

    /// <summary>Precedence: higher binds tighter. It is what decides whether an operand needs parentheses.</summary>
    public int Precedence { get; }

    /// <summary>
    /// Whether reassociating the right operand preserves the semantics. It only holds for the
    /// boolean operators: with <c>+</c> and <c>*</c> over floating point, dropping the parentheses
    /// from <c>a + (b + c)</c> would change the order of evaluation.
    /// </summary>
    public bool IsAssociative { get; }

    /// <summary>Logical disjunction, <c>||</c> — the weakest precedence.</summary>
    public static readonly DaxOperator Or = new("||", 1, isAssociative: true);
    /// <summary>Logical conjunction, <c>&amp;&amp;</c>.</summary>
    public static readonly DaxOperator And = new("&&", 2, isAssociative: true);

    /// <summary>Equality, <c>=</c> — C#'s <c>==</c>.</summary>
    public static readonly DaxOperator Equal = new("=", 3, isAssociative: false);
    /// <summary>Inequality, <c>&lt;&gt;</c> — C#'s <c>!=</c>.</summary>
    public static readonly DaxOperator NotEqual = new("<>", 3, isAssociative: false);
    /// <summary>Greater than.</summary>
    public static readonly DaxOperator GreaterThan = new(">", 3, isAssociative: false);
    /// <summary>Greater than or equal.</summary>
    public static readonly DaxOperator GreaterThanOrEqual = new(">=", 3, isAssociative: false);
    /// <summary>Less than.</summary>
    public static readonly DaxOperator LessThan = new("<", 3, isAssociative: false);
    /// <summary>Less than or equal.</summary>
    public static readonly DaxOperator LessThanOrEqual = new("<=", 3, isAssociative: false);

    /// <summary>
    /// Text concatenation, <c>&amp;</c>. It is <b>not</b> <c>+</c>: in DAX <c>+</c> converts to a
    /// number, so <c>"1" + "2"</c> is <c>3</c> and not <c>"12"</c>.
    /// </summary>
    public static readonly DaxOperator Concatenate = new("&", 4, isAssociative: false);

    /// <summary>Arithmetic addition. For text, see <see cref="Concatenate"/>.</summary>
    public static readonly DaxOperator Add = new("+", 5, isAssociative: false);
    /// <summary>Subtraction.</summary>
    public static readonly DaxOperator Subtract = new("-", 5, isAssociative: false);
    /// <summary>Multiplication.</summary>
    public static readonly DaxOperator Multiply = new("*", 6, isAssociative: false);
    /// <summary>Division. Division by zero in DAX is an error, not infinity — consider <c>DIVIDE</c>.</summary>
    public static readonly DaxOperator Divide = new("/", 6, isAssociative: false);

    /// <summary>The symbol, for debugging and error messages.</summary>
    public override string ToString() => Symbol;
}
