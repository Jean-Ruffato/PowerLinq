using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

public sealed class DaxSetOperationTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[Id]")] public int Id { get; set; }
        [DaxColumn("Venda[ClienteId]")] public int ClienteId { get; set; }
        [DaxColumn("Venda[Valor]")] public decimal Valor { get; set; }
    }

    [DaxTable("Cliente")]
    private sealed class Cliente
    {
        [DaxColumn("Cliente[Id]")] public int Id { get; set; }
        [DaxColumn("Cliente[Nome]")] public string Nome { get; set; } = "";
    }

    private sealed class VendaCliente
    {
        [DaxColumn("[VendaId]")] public int VendaId { get; set; }
        [DaxColumn("[Cliente]")] public string Cliente { get; set; } = "";
    }

    private sealed class RecordingExecutor(int count = 0) : IDaxQueryExecutor
    {
        public string? LastQuery { get; private set; }

        public Task<List<T>> ExecuteAsync<T>(
            string daxQuery,
            CancellationToken cancellationToken = default) where T : class
        {
            LastQuery = daxQuery;
            return Task.FromResult(new List<T>());
        }

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult<object?>(null);
        }

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult(count);
        }
    }

    [Fact]
    public void Skip_WithOrderBy_UsesExceptAndTopN()
    {
        var table = new DaxTable<Venda>(new RecordingExecutor());

        string dax = table.OrderBy(x => x.Id).Skip(10).Take(5).ToDaxString();

        Assert.Contains("EXCEPT(", dax);
        Assert.Contains("TOPN(10, Venda, Venda[Id], ASC)", dax.ReplaceLineEndings(" ").Replace("\n", " "));
        Assert.Contains("TOPN(", dax);
    }

    [Fact]
    public void Skip_WithoutOrderBy_Throws()
    {
        var table = new DaxTable<Venda>(new RecordingExecutor());

        Assert.Throws<InvalidOperationException>(() => table.Skip(2).ToDaxString());
    }

    [Fact]
    public async Task AnyAsync_UsesServerCount()
    {
        var executor = new RecordingExecutor(count: 1);
        var table = new DaxTable<Venda>(executor);

        Assert.True(await table.AnyAsync(x => x.Valor > 0));
        Assert.Contains("COUNTROWS(FILTER(Venda, Venda[Valor] > 0))", executor.LastQuery);
    }

    [Fact]
    public async Task AllAsync_CountsRowsThatDoNotMatch()
    {
        var executor = new RecordingExecutor(count: 0);
        var table = new DaxTable<Venda>(executor);

        Assert.True(await table.AllAsync(x => x.Valor > 0));
        Assert.Contains("NOT(Venda[Valor] > 0)", executor.LastQuery);
    }

    [Fact]
    public void Join_ProducesServerSideEquijoinAndProjection()
    {
        var executor = new RecordingExecutor();
        var vendas = new DaxTable<Venda>(executor);
        var clientes = new DaxTable<Cliente>(executor);

        string dax = vendas.Join(
            clientes,
            venda => venda.ClienteId,
            cliente => cliente.Id,
            (venda, cliente) => new VendaCliente
            {
                VendaId = venda.Id,
                Cliente = cliente.Nome
            }).ToDaxString();

        Assert.Contains("GENERATE(", dax);
        Assert.Contains("FILTER(", dax);
        Assert.Contains("[__pl_outer_key_0] = [__pl_inner_key_0]", dax);
        Assert.Contains("\"VendaId\", [__pl_outer_0]", dax);
        Assert.Contains("\"Cliente\", [__pl_inner_1]", dax);
    }
}
