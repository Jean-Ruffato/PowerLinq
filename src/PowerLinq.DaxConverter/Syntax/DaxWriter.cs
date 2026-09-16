using System.Text;

namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// Write buffer shared by the tree's nodes.
/// </summary>
/// <remarks>
/// It does not know the node hierarchy and decides no syntax — each node writes itself by calling
/// these primitives. What lives here is only what an isolated node would have no way of knowing:
/// the current indentation and the precedence demanded by the position it occupies.
/// </remarks>
public sealed class DaxWriter
{
    private const int IndentWidth = 4;

    private readonly StringBuilder _builder = new();
    private int _depth;
    private bool _compact;

    /// <summary>The text chunks already closed off by a parameter.</summary>
    /// <remarks>
    /// It stays empty while no <see cref="DaxParameterRef"/> appears, which is why
    /// <c>_slots.Count &gt; 0</c> works as "this tree is parameterized" in <see cref="Render"/>.
    /// </remarks>
    private readonly List<string> _segments = [];

    /// <summary>The slots, in the order they appeared in the text.</summary>
    private readonly List<int> _slots = [];

    /// <summary>Writes the whole tree and returns the DAX. It is the only path from tree to text.</summary>
    /// <exception cref="InvalidOperationException">
    /// The tree contains a <see cref="DaxParameterRef"/>. A tree with a parameter does not have a
    /// single text — it has one per set of values — and returning DAX with the parameter's place
    /// left empty would be invalid syntax handed over as if it were a query. Use
    /// <see cref="RenderTemplate"/>.
    /// </exception>
    public static string Render(IDaxNode node)
    {
        var writer = new DaxWriter();
        node.Write(writer);

        if (writer._slots.Count > 0)
            throw new InvalidOperationException(ParameterizedTreeMessage);

        return writer.ToString();
    }

    /// <summary>
    /// Writes the tree, cutting the text at each <see cref="DaxParameterRef"/>, and returns the
    /// chunks — what is left is constant and can be cached.
    /// </summary>
    /// <remarks>
    /// A tree with no parameters at all goes through here too: it becomes a template of one chunk
    /// and zero slots, and binding values to it returns that very chunk. There is no special case.
    /// </remarks>
    public static DaxTemplate RenderTemplate(IDaxNode node)
    {
        var writer = new DaxWriter();
        node.Write(writer);

        // The last chunk — the text after the last parameter — is only closed here: `Placeholder`
        // closes the chunk BEFORE each parameter, and nothing tells the writer it has finished.
        writer._segments.Add(writer._builder.ToString());

        return new DaxTemplate(writer._segments, writer._slots);
    }

    /// <summary><see cref="Render"/>'s message about a parameterized tree.</summary>
    /// <remarks>
    /// It does not come from <c>IPowerLinqLocalizer</c>: the writer takes no localizer — it is the
    /// only path from tree to text and knows nothing about query context — and this condition is a
    /// misuse of the public API, not a refusal an end user reads.
    /// </remarks>
    private const string ParameterizedTreeMessage =
        "This tree carries query parameters, so it has no single DAX text — it has one per set of "
        + "values. Use DaxWriter.RenderTemplate and bind the values, which is what "
        + "DaxCompiledQuery does.";

    /// <summary>The DAX written so far.</summary>
    public override string ToString() => _builder.ToString();

    /// <summary>Appends the text as it is, without escaping or indenting.</summary>
    public DaxWriter Append(string text)
    {
        _builder.Append(text);
        return this;
    }

    /// <summary>Appends the character.</summary>
    public DaxWriter Append(char value)
    {
        _builder.Append(value);
        return this;
    }

    /// <summary>
    /// Appends the integer. It gets its own overload to avoid the caller's <c>ToString()</c>, which
    /// would use the ambient culture — and in DAX the wrong decimal separator changes the meaning.
    /// </summary>
    public DaxWriter Append(int value)
    {
        _builder.Append(value);
        return this;
    }

    /// <summary>Asks the node to write itself. This is how the tree's recursion happens.</summary>
    public DaxWriter Write(IDaxNode node)
    {
        node.Write(this);
        return this;
    }

    /// <summary>Indented line break; emits nothing in compact mode.</summary>
    public DaxWriter Break()
    {
        if (!_compact)
            _builder.Append('\n').Append(' ', _depth * IndentWidth);

        return this;
    }

    /// <summary>Comma between arguments, with a line break outside compact mode.</summary>
    public DaxWriter Separator()
    {
        _builder.Append(',');

        if (_compact)
            _builder.Append(' ');
        else
            Break();

        return this;
    }

    /// <summary>
    /// Closes the current text chunk and records that a parameter goes here.
    /// </summary>
    /// <param name="slot">The parameter's position in the list of values.</param>
    /// <remarks>
    /// Nothing is written. The buffer is <b>cleared</b> rather than marked by position: keeping
    /// offsets inside a single text would force binding to slice the string and recompute every
    /// following offset for each value of a different length. Ready-made chunks turn binding into
    /// a concatenation.
    /// </remarks>
    public DaxWriter Placeholder(int slot)
    {
        _segments.Add(_builder.ToString());
        _builder.Clear();
        _slots.Add(slot);

        return this;
    }

    /// <summary>Increases the indentation by one level, applied at the next break.</summary>
    public DaxWriter Indent()
    {
        _depth++;
        return this;
    }

    /// <summary>Decreases the indentation by one level.</summary>
    public DaxWriter Outdent()
    {
        _depth--;
        return this;
    }

    /// <summary>
    /// Writes an operand, parenthesizing only when the node's precedence is lower than the one
    /// demanded by the position it occupies.
    /// </summary>
    public DaxWriter Operand(IDaxExpression node, int minPrecedence) =>
        node.Precedence >= minPrecedence
            ? Write(node)
            : Append('(').Write(node).Append(')');

    /// <summary>
    /// Writes <c>Name(arg, arg, ...)</c>, suspending line breaks: the arguments of a call are short
    /// and gain nothing from being broken up.
    /// </summary>
    public DaxWriter WriteCall(string name, IReadOnlyList<IDaxNode> arguments)
    {
        Append(name).Append('(');

        bool wasCompact = _compact;
        _compact = true;

        for (int i = 0; i < arguments.Count; i++)
        {
            if (i > 0)
                Separator();

            Write(arguments[i]);
        }

        _compact = wasCompact;

        return Append(')');
    }
}
