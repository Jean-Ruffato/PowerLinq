namespace PowerLinq.DaxConverter.Model;

/// <summary>Which side of a relationship a table sits on.</summary>
/// <remarks>
/// The values are the ones from <c>$SYSTEM.TMSCHEMA_RELATIONSHIPS</c>, not a numbering of our own:
/// reading the DMV converts directly, and renumbering here would only create a map to maintain.
/// </remarks>
public enum DaxCardinality
{
    /// <summary>One. The dimension side, in a star model.</summary>
    One = 1,

    /// <summary>Many. The fact side.</summary>
    Many = 2
}
