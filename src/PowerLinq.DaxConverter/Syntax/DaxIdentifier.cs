namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// Quoting rules for DAX identifiers.
/// </summary>
/// <remarks>
/// <para>
/// In DAX, a table name with a space or a special character needs single quotes:
/// <c>EVALUATE Ordem de Venda</c> is a syntax error, <c>EVALUATE 'Ordem de Venda'</c> is not.
/// Table names with spaces are the norm in Portuguese Power BI models, so emitting the raw name
/// broke the query on the server.
/// </para>
/// <para>
/// Quoting happens on <b>write</b>, not in storage: <see cref="DaxTableRef.Name"/> keeps the name
/// exactly as the user declared it, so the tree stays inspectable and comparable. A name already
/// quoted in the attribute is not quoted twice.
/// </para>
/// </remarks>
public static class DaxIdentifier
{
    /// <summary>
    /// Returns the name ready for DAX, in single quotes when necessary and with any inner quote
    /// doubled.
    /// </summary>
    public static string Quote(string name)
    {
        if (IsAlreadyQuoted(name))
            return name;

        return RequiresQuoting(name)
            ? $"'{name.Replace("'", "''")}'"
            : name;
    }

    /// <summary>
    /// Builds <c>Table[Column]</c>, quoting the table and escaping any <c>]</c> inside the column
    /// name, which DAX escapes by doubling.
    /// </summary>
    /// <remarks>
    /// This is the only place that composes the reference by convention. Both the predicate
    /// translation and the result mapping go through here, because both have to produce exactly
    /// the same string — if they diverge, the query works and materialization silently fails to
    /// find the column.
    /// </remarks>
    public static string Column(string tableName, string columnName) =>
        $"{Quote(tableName)}[{EscapeColumn(columnName)}]";

    /// <summary>
    /// The same reference as <see cref="Column"/>, but without quoting the table. The server
    /// returns the column name in this form in the result metadata, so the mapping records both
    /// and matches whichever one arrives.
    /// </summary>
    public static string UnquotedColumn(string tableName, string columnName) =>
        $"{Unquote(tableName)}[{EscapeColumn(columnName)}]";

    /// <summary>
    /// The table of a <c>Table[Column]</c> reference, unquoted, or <see langword="null"/> when the
    /// reference names no table — an extension column's <c>[Alias]</c>, or a bare name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Knowing which table owns the column is what decides the <b>shape</b> of the filter:
    /// <c>FILTER</c> opens a row context on the iterated table, and in that context a column from
    /// another table is a DAX error. Without this information, a <c>Where</c> over a dimension
    /// column produced <c>FILTER(fact, 'DIM'[col] = ...)</c>, which the server rejects.
    /// </para>
    /// <para>
    /// A malformed reference returns <see langword="null"/> instead of blowing up: it is treated as
    /// a column of the current context, which is exactly the behaviour that predated this function.
    /// </para>
    /// </remarks>
    public static string? TableOf(string reference)
    {
        if (reference.Length == 0 || reference[0] == '[')
            return null;

        int bracket = reference[0] == '\''
            ? ClosingQuote(reference) + 1
            : reference.IndexOf('[');

        return bracket <= 0 || bracket >= reference.Length || reference[bracket] != '['
            ? null
            : Unquote(reference[..bracket]);
    }

    /// <summary>
    /// The column of a <c>Table[Column]</c> or <c>[Alias]</c> reference, without the brackets and
    /// with <c>]]</c> undone, or <see langword="null"/> when the reference has no brackets.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="TableOf"/>. It exists so a reference can be <b>rewritten</b>
    /// in the other spelling: what the user writes in the attribute is used as the key for reading
    /// the result, and ADOMD returns the column name with the table unquoted — <c>T[C]</c> — while
    /// the DAX convention is <c>'T'[C]</c>. Without decomposing it there is no way to register both.
    /// </remarks>
    public static string? ColumnOf(string reference)
    {
        int open = reference.Length > 0 && reference[0] == '\''
            ? ClosingQuote(reference) + 1
            : reference.IndexOf('[');

        if (open < 0 || open >= reference.Length || reference[open] != '[' || !reference.EndsWith(']'))
            return null;

        return reference[(open + 1)..^1].Replace("]]", "]");
    }

    /// <summary>
    /// Compares table names the way the engine compares them: unquoted and case-insensitively,
    /// since an object name in a tabular model is not case sensitive.
    /// </summary>
    public static bool SameTable(string left, string right) =>
        string.Equals(Unquote(left), Unquote(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Index of the quote that closes a quoted name. A doubled quote (<c>''</c>) is a literal quote
    /// inside the name, so it closes nothing.
    /// </summary>
    private static int ClosingQuote(string reference)
    {
        for (int i = 1; i < reference.Length; i++)
        {
            if (reference[i] != '\'')
                continue;

            if (i + 1 < reference.Length && reference[i + 1] == '\'')
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    private static string EscapeColumn(string columnName) => columnName.Replace("]", "]]");

    private static string Unquote(string name) =>
        IsAlreadyQuoted(name) ? name[1..^1].Replace("''", "'") : name;

    private static bool IsAlreadyQuoted(string name) =>
        name.Length >= 2 && name[0] == '\'' && name[^1] == '\'';

    /// <summary>
    /// An unquoted DAX identifier accepts letters, digits and underscores, and does not start with
    /// a digit. Anything else — a space, a hyphen, an accent outside a letter, punctuation —
    /// requires quotes.
    /// </summary>
    private static bool RequiresQuoting(string name)
    {
        if (name.Length == 0)
            return true;

        if (!char.IsLetter(name[0]) && name[0] != '_')
            return true;

        foreach (char character in name)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
                return true;
        }

        return false;
    }
}
