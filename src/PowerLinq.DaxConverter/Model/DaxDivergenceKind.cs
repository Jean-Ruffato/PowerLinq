namespace PowerLinq.DaxConverter.Model;

/// <summary>What a divergence between contract and model is.</summary>
public enum DaxDivergenceKind
{
    /// <summary>The contract declares a table the model does not have.</summary>
    TableNotInModel,

    /// <summary>The contract declares a column the model's table does not have.</summary>
    ColumnNotInModel,

    /// <summary>The measure name does not exist in the model.</summary>
    MeasureNotInModel,

    /// <summary>There is more than one active path between two tables.</summary>
    AmbiguousRelationship,

    /// <summary>The only path between two tables is inactive.</summary>
    InactiveRelationship,

    /// <summary>There is no path between the two tables.</summary>
    NoRelationship,

    /// <summary>The navigation asks for a direction <c>RELATED</c> does not cross.</summary>
    UntraversableDirection
}
