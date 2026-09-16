using PowerLinq.DaxConverter.Attributes;

namespace PowerLinq.Benchmark.Model;

/// <summary>
/// The same contract as <see cref="Produto"/>, without a parameterless constructor — which makes
/// materialization use the constructor instead of assigning properties.
/// </summary>
/// <remarks>
/// It exists so the benchmark can measure the difference between the two paths over <b>the same
/// columns</b>. Comparing against a contract with a different number of properties would measure
/// the column count along with the path, and the two variations would be indistinguishable in the
/// result.
/// </remarks>
public sealed record ProdutoImutavel(
    [property: DaxColumn("Produto[ProdutoID]")] int ProdutoId,
    [property: DaxColumn("Produto[Nome]")] string Nome,
    [property: DaxColumn("Produto[Categoria]")] string Categoria,
    [property: DaxColumn("Produto[Preco]")] decimal Preco,
    [property: DaxColumn("Produto[Quantidade]")] int Quantidade,
    [property: DaxColumn("Produto[Ativo]")] bool Ativo,
    [property: DaxColumn("Produto[DataCadastro]")] DateTime DataCadastro,
    [property: DaxColumn("Produto[Desconto]")] decimal? Desconto);
