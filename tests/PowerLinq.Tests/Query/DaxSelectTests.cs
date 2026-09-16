using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Mapping;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

public sealed class DaxSelectTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Preco]")] public decimal Preco { get; set; }
    }

    private sealed class ProdutoResponse
    {
        public int Codigo { get; set; }
        public string Descricao { get; set; } = "";
        public decimal PrecoComTaxa { get; set; }
    }

    private sealed class RecordingExecutor : IDaxQueryExecutor
    {
        public string? LastQuery { get; private set; }

        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class
        {
            LastQuery = daxQuery;
            return Task.FromResult(new List<T>());
        }

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    [Fact]
    public void Select_ProjectsEntityIntoContractWithServerExpressions()
    {
        var table = new DaxTable<Produto>(new RecordingExecutor());

        string dax = table
            .Where(x => x.Preco > 10)
            .Select(x => new ProdutoResponse
            {
                Codigo = x.Id,
                Descricao = x.Nome,
                PrecoComTaxa = x.Preco * 1.1m
            })
            .ToDaxString();

        Assert.Contains("SELECTCOLUMNS(", dax);
        Assert.Contains("FILTER(", dax);
        Assert.Contains("\"Codigo\", Produto[Id]", dax);
        Assert.Contains("\"Descricao\", Produto[Nome]", dax);
        Assert.Contains("\"PrecoComTaxa\", Produto[Preco] * 1.1", dax);
    }

    [Fact]
    public async Task Select_ToListAsync_SendsProjectionToExecutor()
    {
        var executor = new RecordingExecutor();
        var table = new DaxTable<Produto>(executor);

        await table.Select(x => new ProdutoResponse
        {
            Codigo = x.Id,
            Descricao = x.Nome
        }).ToListAsync();

        Assert.Contains("SELECTCOLUMNS(", executor.LastQuery);
    }

    [Fact]
    public void EntityMapper_MapsProjectedColumnsByPropertyNameWithoutAttributes()
    {
        var result = new DaxResult(
            ["[Codigo]", "[Descricao]"],
            [new DaxRow(new Dictionary<string, object?>
            {
                ["[Codigo]"] = 42,
                ["[Descricao]"] = "Monitor"
            })]);

        ProdutoResponse item = Assert.Single(EntityMapper.MapResult<ProdutoResponse>(result));

        Assert.Equal(42, item.Codigo);
        Assert.Equal("Monitor", item.Descricao);
    }

    [Fact]
    public void Select_WithoutObjectInitializer_Throws()
    {
        var table = new DaxTable<Produto>(new RecordingExecutor());

        Assert.Throws<NotSupportedException>(() => table.Select(x => new ProdutoResponse()));
    }
}
