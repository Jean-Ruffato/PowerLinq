using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Mapping;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Aggregation ergonomics: the attribute stops being mandatory on every property, and the numeric
/// overloads stop forcing <see cref="double"/>.
/// </summary>
public sealed class DaxAggregationErgonomicsTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("Venda[Cliente]")] public string Cliente { get; set; } = "";
        [DaxColumn("Venda[Valor]")] public decimal Valor { get; set; }
        [DaxColumn("Venda[Quantidade]")] public int Quantidade { get; set; }
        [DaxColumn("Venda[Data]")] public DateTime Data { get; set; }
    }

    /// <summary>A contract with no attribute at all — what the convention has to support.</summary>
    private sealed class Totais
    {
        public decimal Total { get; set; }
        public long Qtd { get; set; }
    }

    private sealed class PorCategoria
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        public decimal Total { get; set; }
    }

    private sealed class NoopExecutor : IDaxQueryExecutor
    {
        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class => Task.FromResult(new List<T>());

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private static DaxTable<Venda> Table() => new(new NoopExecutor());

    // ---------- the attribute stops being mandatory ----------

    [Fact]
    public void Aggregate_WithNoAttributesAtAll_UsesPropertyNames()
    {
        string dax = Table()
            .Aggregate(g => new Totais { Total = g.Sum(x => x.Valor), Qtd = g.Count() })
            .ToDaxString();

        Assert.Contains("\"Total\", SUM(Venda[Valor])", dax);
        Assert.Contains("\"Qtd\", COUNTROWS(Venda)", dax);
    }

    [Fact]
    public void GroupBy_WithConventionOnlyExtensions_Works()
    {
        string dax = Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(x => x.Valor) })
            .ToDaxString();

        Assert.Contains("Venda[Categoria]", dax);
        Assert.Contains("\"Total\", SUM(Venda[Valor])", dax);
    }

    [Fact]
    public void AttributeStillOverridesTheName()
    {
        string dax = Table()
            .Aggregate(g => new ComAliasExplicito { Soma = g.Sum(x => x.Valor) })
            .ToDaxString();

        Assert.Contains("\"total_geral\", SUM(Venda[Valor])", dax);
        Assert.DoesNotContain("\"Soma\"", dax);
    }

    private sealed class ComAliasExplicito
    {
        [DaxColumn("[total_geral]")] public decimal Soma { get; set; }
    }

    [Fact]
    public void ConventionName_RoundTripsThroughMaterialization()
    {
        // What guarantees the change is useful and not just compiling: the name the DAX asks for must
        // be the one the mapping knows. Otherwise the query runs and the value comes back default.
        var row = new DaxRow(new Dictionary<string, object?>
        {
            ["Total"] = 150.5m,
            ["Qtd"] = 3L
        });

        Totais materializado = EntityMapper.MapRow<Totais>(
            row,
            EntityMapper.GetColumnMappings(typeof(Totais)));

        Assert.Equal(150.5m, materializado.Total);
        Assert.Equal(3L, materializado.Qtd);
    }

    [Fact]
    public void ConventionName_AlsoMatchesTheBracketedForm()
    {
        // The server may report the extension column as [Total]; the mapping registers both
        // shapes.
        var row = new DaxRow(new Dictionary<string, object?> { ["[Total]"] = 42m });

        Totais materializado = EntityMapper.MapRow<Totais>(
            row,
            EntityMapper.GetColumnMappings(typeof(Totais)));

        Assert.Equal(42m, materializado.Total);
    }

    // ---------- overloads that preserve the type ----------

    [Fact]
    public void Min_OverADecimalColumn_ReturnsDecimalWithoutACast()
    {
        // The point: decimal does not convert implicitly to double, so this used to require a
        // cast in the lambda — and the return came back as double, reintroducing rounding error
        // exactly where decimal was chosen to avoid it.
        string dax = Table()
            .Aggregate(g => new MinMaxDecimal
            {
                Menor = g.Min(x => x.Valor),
                Maior = g.Max(x => x.Valor)
            })
            .ToDaxString();

        Assert.Contains("\"Menor\", MIN(Venda[Valor])", dax);
        Assert.Contains("\"Maior\", MAX(Venda[Valor])", dax);
    }

    private sealed class MinMaxDecimal
    {
        public decimal Menor { get; set; }
        public decimal Maior { get; set; }
    }

    [Fact]
    public void Average_OverADecimalColumn_ReturnsDecimal()
    {
        string dax = Table()
            .Aggregate(g => new MediaDecimal { Media = g.Average(x => x.Valor) })
            .ToDaxString();

        Assert.Contains("\"Media\", AVERAGE(Venda[Valor])", dax);
    }

    private sealed class MediaDecimal
    {
        public decimal Media { get; set; }
    }

    [Fact]
    public void MinAndMax_OverAnIntColumn_ReturnInt()
    {
        string dax = Table()
            .Aggregate(g => new MinMaxInt
            {
                Menor = g.Min(x => x.Quantidade),
                Maior = g.Max(x => x.Quantidade)
            })
            .ToDaxString();

        Assert.Contains("\"Menor\", MIN(Venda[Quantidade])", dax);
        Assert.Contains("\"Maior\", MAX(Venda[Quantidade])", dax);
    }

    private sealed class MinMaxInt
    {
        public int Menor { get; set; }
        public int Maior { get; set; }
    }

    [Fact]
    public void MinAndMax_OverADateColumn_AreNowExpressible()
    {
        // MIN/MAX over a date are valid in DAX and previously had no way of being written.
        string dax = Table()
            .Aggregate(g => new Periodo
            {
                Primeira = g.Min(x => x.Data),
                Ultima = g.Max(x => x.Data)
            })
            .ToDaxString();

        Assert.Contains("\"Primeira\", MIN(Venda[Data])", dax);
        Assert.Contains("\"Ultima\", MAX(Venda[Data])", dax);
    }

    private sealed class Periodo
    {
        public DateTime Primeira { get; set; }
        public DateTime Ultima { get; set; }
    }

    [Fact]
    public void ComputedExpression_StillUsesTheIteratorForm()
    {
        // A pure column uses the scalar form; an expression uses the iterating one. The new
        // overloads must not have changed that choice.
        string dax = Table()
            .Aggregate(g => new MediaDecimal { Media = g.Average(x => x.Valor * 1.1m) })
            .ToDaxString();

        Assert.Contains("AVERAGEX(Venda, Venda[Valor] * 1.1)", dax);
    }

    // ---------- DISTINCTCOUNT ----------

    [Fact]
    public void CountDistinct_OverAColumn_BecomesDistinctCount()
    {
        string dax = Table()
            .Aggregate(g => new Clientes { Distintos = g.CountDistinct(x => x.Cliente) })
            .ToDaxString();

        Assert.Contains("\"Distintos\", DISTINCTCOUNT(Venda[Cliente])", dax);
    }

    [Fact]
    public void CountDistinct_OverAnExpression_IsRefusedWithAClearMessage()
    {
        // DAX has no DISTINCTCOUNTX: counting distinct values of a computed expression would
        // change the semantics, so it is refused instead of translated wrong.
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => Table()
            .Aggregate(g => new Clientes { Distintos = g.CountDistinct(x => x.Cliente.ToUpper()) })
            .ToDaxString());

        Assert.Contains("must reference a column", ex.Message);
    }

    [Fact]
    public void CountDistinctMessage_IsLocalized()
    {
        IPowerLinqLocalizer portuguese = new ResourceManagerPowerLinqLocalizer("pt-BR");

        Assert.Contains(
            "precisa referenciar uma coluna",
            portuguese.Get("CountDistinctRequiresColumn"));
    }

    private sealed class Clientes
    {
        public long Distintos { get; set; }
    }

    // ---------- arithmetic between aggregates ----------

    private sealed class Combinados
    {
        public decimal Resultado { get; set; }
    }

    /// <summary>
    /// The four arithmetic operators compose aggregates inside the same extension column. Only the
    /// sum and the division had a test; a mapping error in subtraction or multiplication would come
    /// out as valid DAX computing something else.
    /// </summary>
    [Fact]
    public void Aggregates_ComposeWithEveryArithmeticOperator()
    {
        static string Dax(Func<DaxQuery<Combinados>> build) =>
            string.Join(' ', build().ToDaxString()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains(
            "SUM(Venda[Valor]) - SUM(Venda[Quantidade])",
            Dax(() => Table().Aggregate(g => new Combinados
            {
                Resultado = g.Sum(x => x.Valor) - g.Sum(x => x.Quantidade)
            })),
            StringComparison.Ordinal);

        Assert.Contains(
            "SUM(Venda[Valor]) * SUM(Venda[Quantidade])",
            Dax(() => Table().Aggregate(g => new Combinados
            {
                Resultado = g.Sum(x => x.Valor) * g.Sum(x => x.Quantidade)
            })),
            StringComparison.Ordinal);
    }

    // ---------- what the aggregation refuses ----------

    /// <summary>
    /// The selector has to be <c>g =&gt; new Resultado { ... }</c>. With no initializer there is no
    /// extension column name to emit, and a <c>SUMMARIZECOLUMNS</c> with no extension at all would
    /// return a different query.
    /// </summary>
    [Fact]
    public void Aggregate_WithoutAnObjectInitializer_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table().Aggregate(g => new Totais()).ToDaxString());

        Assert.Contains("must be 'g => new Result { ... }'", ex.Message, StringComparison.Ordinal);
    }

    private sealed class TotaisAninhados
    {
        public Totais Interno { get; set; } = new();
    }

    /// <summary>
    /// The initializer has to <b>assign</b> each member. The nested shape
    /// <c>new X { Interno = { ... } }</c> is a <c>MemberMemberBinding</c>, not an assignment, and
    /// there is no extension column to name from it — the <c>SUMMARIZECOLUMNS</c> would come out
    /// without the column the code asked for.
    /// </summary>
    [Fact]
    public void Aggregate_WithANestedInitializerInsteadOfAnAssignment_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table()
                .Aggregate(g => new TotaisAninhados { Interno = { Total = g.Sum(x => x.Valor) } })
                .ToDaxString());

        Assert.Contains("Only property assignments", ex.Message, StringComparison.Ordinal);
    }
}
