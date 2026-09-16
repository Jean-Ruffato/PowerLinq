using PowerLinq.DaxConverter.Attributes;

namespace PowerLinq.Benchmark.Model;

/// <summary>
/// The benchmarks' reference entity. It covers every branch of
/// <c>DaxExpressionVisitor.AppendValue</c> and of <c>EntityMapper.MapRow</c>: value types, string,
/// bool, DateTime, nullable, and one property with no <see cref="DaxColumnAttribute"/> (which
/// exercises the Table[Property] fallback).
/// </summary>
[DaxTable("Produto")]
public sealed class Produto
{
    [DaxColumn("Produto[ProdutoID]")]
    public int ProdutoId { get; set; }

    [DaxColumn("Produto[Nome]")]
    public string Nome { get; set; } = string.Empty;

    [DaxColumn("Produto[Categoria]")]
    public string Categoria { get; set; } = string.Empty;

    [DaxColumn("Produto[Preco]")]
    public decimal Preco { get; set; }

    [DaxColumn("Produto[Quantidade]")]
    public int Quantidade { get; set; }

    [DaxColumn("Produto[Ativo]")]
    public bool Ativo { get; set; }

    [DaxColumn("Produto[DataCadastro]")]
    public DateTime DataCadastro { get; set; }

    [DaxColumn("Produto[Desconto]")]
    public decimal? Desconto { get; set; }

    /// <summary>No attribute: falls back to <c>Produto[Observacao]</c>.</summary>
    public string Observacao { get; set; } = string.Empty;
}
