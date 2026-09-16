using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;
using Syntax = PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.Tests.Query;

/// <summary>
/// The join became a pipeline <b>stage</b>, so <c>Join</c> returns a
/// <see cref="DaxQuery{TResult}"/> and comes off <see cref="DaxQuery{T}"/> — not only off the table.
/// </summary>
/// <remarks>
/// <para>
/// The change that matters is <b>what the outer side is</b>. While the join was a terminal class
/// created from an <c>IDaxTable</c>, the outer side could only be the whole table: there was no
/// way to filter before joining. As a stage, the outer side is the accumulated source, and
/// <c>Where(...).Join(...)</c> joins the already-filtered table.
/// </para>
/// <para>
/// The internal aliases (<c>__pl_outer_key</c>, <c>__pl_outer_0</c>, ...) are not style:
/// <c>GENERATE</c> has no join clause, so both sides are reduced to columns of known name so the
/// <c>FILTER</c> can compare the keys.
/// </para>
/// </remarks>
public sealed class DaxJoinStageTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[Id]")] public int Id { get; set; }
        [DaxColumn("Venda[ClienteId]")] public int ClienteId { get; set; }
        [DaxColumn("Venda[Valor]")] public decimal Valor { get; set; }
        [DaxColumn("Venda[Ativo]")] public bool Ativo { get; set; }
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

    private sealed class RecordingExecutor(int rows = 0) : IDaxQueryExecutor
    {
        public string? LastQuery { get; private set; }

        public Task<List<TRow>> ExecuteAsync<TRow>(string daxQuery, CancellationToken cancellationToken = default)
            where TRow : class
        {
            LastQuery = daxQuery;
            return Task.FromResult(Enumerable.Range(0, rows).Select(_ => Activator.CreateInstance<TRow>()).ToList());
        }

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult<object?>(rows);
        }

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult(rows);
        }
    }

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    private static DaxQuery<VendaCliente> Join(
        DaxTable<Venda> vendas,
        DaxTable<Cliente> clientes) =>
        vendas.Join(
            clientes,
            v => v.ClienteId,
            c => c.Id,
            (v, c) => new VendaCliente { VendaId = v.Id, Cliente = c.Nome });

    // ---------- the DAX ----------

    [Fact]
    public void Join_ReducesBothSidesToKnownAliasesAndCrossesWithGenerate()
    {
        var executor = new RecordingExecutor();
        string dax = Flat(Join(new DaxTable<Venda>(executor), new DaxTable<Cliente>(executor)).ToDaxString());

        Assert.Contains("GENERATE(", dax, StringComparison.Ordinal);
        Assert.Contains("[__pl_outer_key_0] = [__pl_inner_key_0]", dax, StringComparison.Ordinal);
        // One SELECTCOLUMNS per side, plus the outer one that renames onto the contract.
        Assert.Equal(3, dax.Split("SELECTCOLUMNS(").Length - 1);
        Assert.Contains("\"VendaId\", [__pl_outer_0]", dax, StringComparison.Ordinal);
        Assert.Contains("\"Cliente\", [__pl_inner_1]", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// The test that justifies the slice: the outer side is the <b>accumulated</b> source, so the
    /// <c>FILTER</c> goes inside the outer <c>SELECTCOLUMNS</c> instead of not existing at all.
    /// </summary>
    [Fact]
    public void Join_AfterWhere_JoinsTheFilteredTable()
    {
        var executor = new RecordingExecutor();
        var vendas = new DaxTable<Venda>(executor);

        string dax = Flat(vendas
            .Where(v => v.Ativo)
            .Join(
                new DaxTable<Cliente>(executor),
                v => v.ClienteId,
                c => c.Id,
                (v, c) => new VendaCliente { VendaId = v.Id, Cliente = c.Nome })
            .ToDaxString());

        Assert.Contains(
            "SELECTCOLUMNS(FILTER(Venda, Venda[Ativo]), \"__pl_outer_key_0\"",
            dax,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Join_AfterWhereAndWindow_JoinsTheWindow()
    {
        var executor = new RecordingExecutor();
        var vendas = new DaxTable<Venda>(executor);

        string dax = Flat(vendas
            .Where(v => v.Ativo)
            .OrderBy(v => v.Id)
            .Take(10)
            .Join(
                new DaxTable<Cliente>(executor),
                v => v.ClienteId,
                c => c.Id,
                (v, c) => new VendaCliente { VendaId = v.Id, Cliente = c.Nome })
            .ToDaxString());

        Assert.Contains("SELECTCOLUMNS(TOPN(10, FILTER(Venda, Venda[Ativo])", dax, StringComparison.Ordinal);
    }

    // ---------- the operators the join came to inherit ----------

    [Fact]
    public async Task CountAsync_AfterJoin_CountsTheJoinedRows()
    {
        var executor = new RecordingExecutor(rows: 7);

        int total = await Join(new DaxTable<Venda>(executor), new DaxTable<Cliente>(executor))
            .CountAsync();

        Assert.Equal(7, total);
        Assert.StartsWith("EVALUATE ROW(\"[Count]\", COUNTROWS(SELECTCOLUMNS(GENERATE(",
            Flat(executor.LastQuery!));
    }

    [Fact]
    public async Task Take_AfterJoin_LimitsTheResult()
    {
        var executor = new RecordingExecutor(rows: 2);

        await Join(new DaxTable<Venda>(executor), new DaxTable<Cliente>(executor))
            .Take(5)
            .ToListAsync();

        Assert.StartsWith("EVALUATE TOPN(5, SELECTCOLUMNS(GENERATE(", Flat(executor.LastQuery!));
    }

    [Fact]
    public async Task ToArrayAsync_AfterJoin_Materializes()
    {
        var executor = new RecordingExecutor(rows: 3);

        VendaCliente[] linhas = await Join(new DaxTable<Venda>(executor), new DaxTable<Cliente>(executor))
            .ToArrayAsync();

        Assert.Equal(3, linhas.Length);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_AfterJoin_NarrowsToOneRow()
    {
        var executor = new RecordingExecutor(rows: 1);

        await Join(new DaxTable<Venda>(executor), new DaxTable<Cliente>(executor)).FirstOrDefaultAsync();

        Assert.StartsWith("EVALUATE TOPN(1, SELECTCOLUMNS(GENERATE(", Flat(executor.LastQuery!));
    }

    // ---------- what the join refuses ----------

    /// <summary>
    /// Chaining joins works: the outer side's key and projections are resolved against the previous
    /// join's result columns.
    /// </summary>
    [Fact]
    public void Join_AfterJoin_ResolvesTheKeyAgainstThePreviousResult()
    {
        var executor = new RecordingExecutor();
        var clientes = new DaxTable<Cliente>(executor);

        string dax = Flat(Join(new DaxTable<Venda>(executor), clientes)
            .Join(
                clientes,
                r => r.VendaId,
                c => c.Id,
                (r, c) => new VendaCliente { VendaId = r.VendaId, Cliente = c.Nome })
            .ToDaxString());

        // The second join's key comes out as [VendaId] — a column of the first one's result — and
        // not Venda[Id], which no longer exists there.
        Assert.Contains("\"__pl_outer_key_0\", [VendaId]", dax, StringComparison.Ordinal);
        // Two nested GENERATEs.
        Assert.Equal(2, dax.Split("GENERATE(").Length - 1);
    }

    /// <summary>
    /// A <c>Where</c> after the join filters its result, with the output reference.
    /// </summary>
    [Fact]
    public void Where_AfterJoin_FiltersTheJoinedResult()
    {
        var executor = new RecordingExecutor();

        string dax = Flat(Join(new DaxTable<Venda>(executor), new DaxTable<Cliente>(executor))
            .Where(r => r.Cliente == "x")
            .ToDaxString());

        Assert.StartsWith("EVALUATE FILTER(SELECTCOLUMNS(GENERATE(", dax);
        Assert.EndsWith("[Cliente] = \"x\")", dax);
        // The reference is the join's OUTPUT one, not the source table's.
        Assert.DoesNotContain("Cliente[Nome] = \"x\"", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ordering <b>after</b> the join works, resolving against its output columns — the
    /// <c>ORDER BY</c> references <c>[Cliente]</c>, and not <c>Cliente[Nome]</c>, which the join's
    /// result does not carry.
    /// </summary>
    [Fact]
    public void OrderBy_AfterJoin_OrdersByTheOutputColumn()
    {
        var executor = new RecordingExecutor();

        string dax = Flat(Join(new DaxTable<Venda>(executor), new DaxTable<Cliente>(executor))
            .OrderBy(r => r.Cliente)
            .ToDaxString());

        Assert.EndsWith("ORDER BY [Cliente] ASC", dax);
    }

    /// <summary>
    /// Ordering <b>before</b> the join: the term is rewritten onto the corresponding output column.
    /// </summary>
    /// <remarks>
    /// Without the rewrite, the clause came out as <c>ORDER BY Venda[Id]</c> over the join's
    /// <c>SELECTCOLUMNS</c> — a column the result does not carry, and which the server rejects. It
    /// was a defect that came into existence when <c>Join</c> came off <c>IDaxTable</c> and started
    /// being able to have an earlier ordering.
    /// </remarks>
    [Fact]
    public void OrderBy_BeforeJoin_IsRewrittenToTheOutputColumn()
    {
        var executor = new RecordingExecutor();

        string dax = Flat(new DaxTable<Venda>(executor)
            .OrderByDescending(v => v.Id)
            .Join(
                new DaxTable<Cliente>(executor),
                v => v.ClienteId,
                c => c.Id,
                (v, c) => new VendaCliente { VendaId = v.Id, Cliente = c.Nome })
            .ToDaxString());

        Assert.EndsWith("ORDER BY [VendaId] DESC", dax);
        Assert.DoesNotContain("ORDER BY Venda[Id]", dax);
    }

    /// <summary>
    /// Ordering before the join by a column the join's projection does not carry forward is refused
    /// — the same rule as the projection's, for the same reason.
    /// </summary>
    [Fact]
    public void OrderBy_BeforeJoin_ByAColumnTheJoinDrops_IsRefused()
    {
        var executor = new RecordingExecutor();

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new DaxTable<Venda>(executor)
                .OrderByDescending(v => v.Valor)
                .Join(
                    new DaxTable<Cliente>(executor),
                    v => v.ClienteId,
                    c => c.Id,
                    (v, c) => new VendaCliente { VendaId = v.Id, Cliente = c.Nome })
                .ToDaxString());

        Assert.Contains("Venda[Valor]", ex.Message);
        Assert.Contains("Join", ex.Message);
    }

    /// <summary>
    /// Only the <b>outer</b> side's columns receive the earlier ordering. The distinction is
    /// unobservable when the two tables differ — a pending term can only reference what existed
    /// before the join — and becomes observable in a <b>self-join</b>, where both sides have the
    /// same source references.
    /// </summary>
    [Fact]
    public void OrderBy_BeforeASelfJoin_IsRewrittenToTheOuterOutputColumn()
    {
        var executor = new RecordingExecutor();

        string dax = Flat(new DaxTable<Venda>(executor)
            .OrderByDescending(v => v.Id)
            .Join(
                new DaxTable<Venda>(executor),
                v => v.ClienteId,
                outra => outra.Id,
                // Both ends project Venda[Id]: the outer one as [Origem], the inner one as
                // [Destino]. Without the per-side filter, the rewrite would take the last — the inner one.
                (v, outra) => new VendaVenda { Origem = v.Id, Destino = outra.Id })
            .ToDaxString());

        Assert.EndsWith("ORDER BY [Origem] DESC", dax);
    }

    /// <summary>The result of a self-join: both columns come from the same table.</summary>
    private sealed class VendaVenda
    {
        [DaxColumn("[Origem]")] public int Origem { get; set; }
        [DaxColumn("[Destino]")] public int Destino { get; set; }
    }

    // ---------- a computed expression in the projection ----------

    /// <summary>
    /// The expression is evaluated <b>inside its own side</b>, before the cross: the outer
    /// <c>SELECTCOLUMNS</c>'s alias comes to carry the computation, instead of just the column.
    /// </summary>
    [Fact]
    public void Join_WithAComputedOuterProjection_ComputesInsideTheOuterSide()
    {
        var executor = new RecordingExecutor();

        string dax = Flat(new DaxTable<Venda>(executor)
            .Join(
                new DaxTable<Cliente>(executor),
                v => v.ClienteId,
                c => c.Id,
                (v, c) => new VendaValor { Valor = v.Valor * 1.1m, Cliente = c.Nome })
            .ToDaxString());

        Assert.Contains("\"__pl_outer_0\", Venda[Valor] * 1.1", dax, StringComparison.Ordinal);
    }

    [Fact]
    public void Join_WithAComputedInnerProjection_ComputesInsideTheInnerSide()
    {
        var executor = new RecordingExecutor();

        string dax = Flat(new DaxTable<Venda>(executor)
            .Join(
                new DaxTable<Cliente>(executor),
                v => v.ClienteId,
                c => c.Id,
                (v, c) => new VendaValor { Valor = v.Valor, Cliente = c.Nome.ToUpper() })
            .ToDaxString());

        Assert.Contains("\"__pl_inner_1\", UPPER(Cliente[Nome])", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// An expression spanning both sides is refused, and the message points at the way out — which
    /// now exists: project the columns separately and combine them in a <c>Select</c> after the join.
    /// </summary>
    [Fact]
    public void Join_WithAProjectionSpanningBothSides_IsRefused()
    {
        var executor = new RecordingExecutor();

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new DaxTable<Venda>(executor).Join(
                new DaxTable<Cliente>(executor),
                v => v.ClienteId,
                c => c.Id,
                (v, c) => new VendaCliente { VendaId = v.Id, Cliente = c.Nome + v.Id }));

        Assert.Contains("both sides", ex.Message);
        Assert.Contains("Select after the Join", ex.Message);
    }

    /// <summary>
    /// A closed expression references no parameter at all, so where it is evaluated makes no
    /// difference — it goes to the outer side.
    /// </summary>
    [Fact]
    public void Join_WithAClosedProjection_PutsItOnTheOuterSide()
    {
        var executor = new RecordingExecutor();

        string dax = Flat(new DaxTable<Venda>(executor)
            .Join(
                new DaxTable<Cliente>(executor),
                v => v.ClienteId,
                c => c.Id,
                (v, c) => new VendaValor { Valor = 42m, Cliente = c.Nome })
            .ToDaxString());

        Assert.Contains("\"__pl_outer_0\", 42", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ordering before the join by a column the projection turned into a <b>computation</b> is
    /// refused: there is no identifiable source column to rewrite the ordering onto.
    /// </summary>
    [Fact]
    public void OrderBy_BeforeJoin_ByAColumnTheProjectionComputes_IsRefused()
    {
        var executor = new RecordingExecutor();

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new DaxTable<Venda>(executor)
                .OrderByDescending(v => v.Valor)
                .Join(
                    new DaxTable<Cliente>(executor),
                    v => v.ClienteId,
                    c => c.Id,
                    (v, c) => new VendaValor { Valor = v.Valor * 2m, Cliente = c.Nome })
                .ToDaxString());

        Assert.Contains("Venda[Valor]", ex.Message);
        Assert.Contains("Join", ex.Message);
    }

    /// <summary>A result with a numeric column, to exercise a computation in the projection.</summary>
    private sealed class VendaValor
    {
        [DaxColumn("[Valor]")] public decimal Valor { get; set; }
        [DaxColumn("[Cliente]")] public string Cliente { get; set; } = "";
    }

    // ---------- a filter on the inner side ----------

    /// <summary>
    /// The first criterion asked whether the filter discard was reachable. The answer, recorded:
    /// <b>it was not, and it stopped being able to be</b>. While <c>Join</c> only came off
    /// <c>IDaxTable</c>, there was no way to supply a filter on either side; the reshape made the
    /// outer side the accumulated source, and this overload makes the inner one go through the same fold.
    /// </summary>
    [Fact]
    public void Join_WithAFilteredInnerSide_KeepsThatFilterInTheDax()
    {
        var executor = new RecordingExecutor();

        string dax = Flat(new DaxTable<Venda>(executor)
            .Where(v => v.Ativo)
            .Join(
                new DaxTable<Cliente>(executor).Where(c => c.Nome != ""),
                v => v.ClienteId,
                c => c.Id,
                (v, c) => new VendaCliente { VendaId = v.Id, Cliente = c.Nome })
            .ToDaxString());

        // Both filters show up, each on its own side of the GENERATE.
        Assert.Contains("SELECTCOLUMNS(FILTER(Venda, Venda[Ativo])", dax, StringComparison.Ordinal);
        Assert.Contains(
            "SELECTCOLUMNS(FILTER(Cliente, Cliente[Nome] <> \"\")",
            dax,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Join_WithAnUnfilteredInnerSide_StillUsesTheBareTable()
    {
        var executor = new RecordingExecutor();

        string dax = Flat(Join(new DaxTable<Venda>(executor), new DaxTable<Cliente>(executor)).ToDaxString());

        Assert.Contains("SELECTCOLUMNS(Cliente, \"__pl_inner_key_0\"", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// The inner side goes in as the table to cross with, so ordering or limiting it before the
    /// cross would change which rows take part without the DAX expressing it. Refusing during
    /// composition is preferable to discarding silently — which is the anticipated defect.
    /// </summary>
    [Theory]
    [InlineData("Take")]
    [InlineData("OrderBy")]
    [InlineData("Skip")]
    public void Join_WithAnOrderedOrLimitedInnerSide_IsRefused(string @operator)
    {
        var executor = new RecordingExecutor();
        DaxTable<Cliente> clientes = new(executor);

        DaxQuery<Cliente> interno = @operator switch
        {
            "Take" => clientes.Take(5),
            "Skip" => clientes.Skip(5),
            _ => clientes.OrderBy(c => c.Nome)
        };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new DaxTable<Venda>(executor).Join(
                interno,
                v => v.ClienteId,
                c => c.Id,
                (v, c) => new VendaCliente { VendaId = v.Id, Cliente = c.Nome }));

        Assert.Contains("only filters", ex.Message);
        Assert.Contains(@operator, ex.Message);
    }

    /// <summary>
    /// A projection on the inner side falls into the same refusal, and the message says
    /// <c>Select</c>: the inner side goes in as the table to cross with, and projecting it first would change which columns exist for the key.
    /// </summary>
    [Fact]
    public void Join_WithAProjectedInnerSide_IsRefusedNamingSelect()
    {
        var executor = new RecordingExecutor();

        DaxQuery<Cliente> interno = new DaxTable<Cliente>(executor)
            .Select(c => new Cliente { Id = c.Id, Nome = c.Nome });

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new DaxTable<Venda>(executor).Join(
                interno,
                v => v.ClienteId,
                c => c.Id,
                (v, c) => new VendaCliente { VendaId = v.Id, Cliente = c.Nome }));

        Assert.Contains("only filters", ex.Message);
        Assert.Contains("Select", ex.Message);
    }

    private sealed class ProjecaoComCampo
    {
#pragma warning disable CA1051 // O campo público é o ponto do teste: só propriedade é projetável.
        public string Cliente = "";
#pragma warning restore CA1051
    }

    /// <summary>
    /// Only a <b>property</b> is projectable in the join's result. Assigning a field is refused
    /// rather than ignored: the <c>SELECTCOLUMNS</c> would come out without the column, and the
    /// absence would only show up as a default value after materialization.
    /// </summary>
    [Fact]
    public void Join_ProjectingIntoAField_IsRefused()
    {
        var executor = new RecordingExecutor();

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new DaxTable<Venda>(executor).Join(
                new DaxTable<Cliente>(executor),
                v => v.ClienteId,
                c => c.Id,
                (v, c) => new ProjecaoComCampo { Cliente = c.Nome }));

        Assert.Contains("assign properties directly", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The projection can only talk about the join's two sides. A parameter that is neither — a
    /// nested lambda's — is refused, and not translated against a side picked at random.
    /// </summary>
    [Fact]
    public void Join_ProjectingFromAnUnknownParameter_IsRefused()
    {
        var executor = new RecordingExecutor();
        string[] nomes = ["a", "b"];

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new DaxTable<Venda>(executor).Join(
                new DaxTable<Cliente>(executor),
                v => v.ClienteId,
                c => c.Id,
                (v, c) => new VendaCliente
                {
                    VendaId = v.Id,
                    Cliente = nomes.First(nome => nome.Length > 0)
                }));

        Assert.Contains("unknown parameter", ex.Message, StringComparison.Ordinal);
    }

    // ---------- composite key ----------

    [Fact]
    public void Join_WithACompositeKey_ComparesEveryComponent()
    {
        var executor = new RecordingExecutor();

        string dax = Flat(new DaxTable<Venda>(executor)
            .Join(
                new DaxTable<Cliente>(executor),
                v => new { v.ClienteId, v.Id },
                c => new { ClienteId = c.Id, Id = c.Id },
                (v, c) => new VendaCliente { VendaId = v.Id, Cliente = c.Nome })
            .ToDaxString());

        // One alias pair per component, and the FILTER compares both with &&.
        Assert.Contains("\"__pl_outer_key_0\", Venda[ClienteId]", dax, StringComparison.Ordinal);
        Assert.Contains("\"__pl_outer_key_1\", Venda[Id]", dax, StringComparison.Ordinal);
        Assert.Contains(
            "[__pl_outer_key_0] = [__pl_inner_key_0] && [__pl_outer_key_1] = [__pl_inner_key_1]",
            dax,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A different arity on the two sides is <b>impossible to write</b> through the typed API:
    /// <c>TKey</c> is the same type in both selectors, so the anonymous type with two members does
    /// not unify with the one-member one. The check exists in the builder, where a hand-built stage can violate it.
    /// </summary>
    [Fact]
    public void TheBuilder_RefusesMismatchedCompositeKeyArity()
    {
        var pipeline = new DaxPipeline("Venda", typeof(object))
        {
            Stages =
            [
                new DaxJoinStage(
                    "Cliente",
                    [],
                    ["Venda[ClienteId]", "Venda[Id]"],
                    ["Cliente[Id]"],
                    [new DaxJoinProjection("Nome", FromOuter: false, new Syntax.DaxColumnRef("Cliente[Nome]"))])
            ]
        };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => DaxConverter.Builders.DaxPipelineBuilder.Build(pipeline));

        Assert.Contains("same number of components", ex.Message);
    }

    /// <summary>
    /// The invalid-key message quotes <b>Join</b>, not <c>GroupBy</c>. The key resolution is the
    /// grouping's — reused on purpose, because it already understands <c>new { a, b }</c> — but its
    /// default message names an operator the caller never
    /// wrote.
    /// </summary>
    [Fact]
    public void Join_WithAComputedKey_NamesTheJoinAndNotTheGroupBy()
    {
        var executor = new RecordingExecutor();

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new DaxTable<Venda>(executor).Join(
                new DaxTable<Cliente>(executor),
                v => v.ClienteId + 1,
                c => c.Id,
                (v, c) => new VendaCliente { VendaId = v.Id, Cliente = c.Nome }));

        Assert.Contains("Join", ex.Message);
        Assert.DoesNotContain("GroupBy", ex.Message);
    }

    [Fact]
    public void Join_AfterProjection_ResolvesTheKeyAgainstTheProjection()
    {
        var executor = new RecordingExecutor();

        string dax = Flat(new DaxTable<Venda>(executor)
            .Select(v => new VendaCliente { VendaId = v.Id })
            .Join(
                new DaxTable<Cliente>(executor),
                r => r.VendaId,
                c => c.Id,
                (r, c) => new VendaCliente { VendaId = r.VendaId, Cliente = c.Nome })
            .ToDaxString());

        Assert.Contains("\"__pl_outer_key_0\", [VendaId]", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// Chaining by a column the previous join did not carry forward is refused, naming the ones it
    /// did carry.
    /// </summary>
    [Fact]
    public void Join_AfterJoin_ByAColumnTheFirstJoinDropped_IsRefused()
    {
        var executor = new RecordingExecutor();

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new DaxTable<Venda>(executor)
                .Select(v => new VendaCliente { Cliente = "x" })
                .Join(
                    new DaxTable<Cliente>(executor),
                    r => r.VendaId,
                    c => c.Id,
                    (r, c) => new VendaCliente { VendaId = r.VendaId, Cliente = c.Nome }));

        Assert.Contains("VendaCliente.VendaId", ex.Message);
        Assert.Contains("[Cliente]", ex.Message);
    }

    /// <summary>
    /// The inner side's table has to come from the factory: without it there is no table name to
    /// resolve, and a custom implementation of <see cref="IDaxTable{T}"/> would have no way to supply it.
    /// </summary>
    [Fact]
    public void Join_WithAForeignTableImplementation_IsRefused()
    {
        var executor = new RecordingExecutor();

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new DaxTable<Venda>(executor).Join(
                new AlienTable(),
                v => v.ClienteId,
                c => c.Id,
                (v, c) => new VendaCliente { VendaId = v.Id, Cliente = c.Nome }));

        Assert.Contains("IDaxTableFactory", ex.Message);
    }

    [Fact]
    public void Join_WithANullInnerTable_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new DaxTable<Venda>(new RecordingExecutor()).Join<Cliente, int, VendaCliente>(
                (IDaxTable<Cliente>)null!,
                v => v.ClienteId,
                c => c.Id,
                (v, c) => new VendaCliente()));

    [Fact]
    public void Join_WithAComputedKey_IsRefused()
    {
        var executor = new RecordingExecutor();

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new DaxTable<Venda>(executor).Join(
                new DaxTable<Cliente>(executor),
                v => v.ClienteId + 1,
                c => c.Id,
                (v, c) => new VendaCliente { VendaId = v.Id, Cliente = c.Nome }));

        Assert.Contains("single property", ex.Message);
    }

    [Fact]
    public void TheRefusalAfterJoin_IsLocalized()
    {
        var executor = new RecordingExecutor();
        IDaxTableFactory factory = new DaxTableFactory(new ResourceManagerPowerLinqLocalizer("pt-BR"));

        IDaxTable<Venda> vendas = factory.Create<Venda>(executor);
        IDaxTable<Cliente> clientes = factory.Create<Cliente>(executor);

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => vendas
                .Join(clientes, v => v.ClienteId, c => c.Id,
                    (v, c) => new VendaCliente { VendaId = v.Id, Cliente = c.Nome })
                .GroupBy(r => r.Cliente));

        Assert.StartsWith("GroupBy não pode ser aplicado depois de Join", ex.Message);
    }

    /// <summary>An implementation from outside the library, just to exercise the factory refusal.</summary>
    private sealed class AlienTable : IDaxTable<Cliente>
    {
        public DaxQuery<Cliente> Where(System.Linq.Expressions.Expression<Func<Cliente, bool>> predicate) =>
            throw new NotSupportedException();
        public DaxQuery<Cliente> WhereIf(bool condition, System.Linq.Expressions.Expression<Func<Cliente, bool>> predicate) =>
            throw new NotSupportedException();
        public DaxQuery<Cliente> WhereIf(string? value, System.Linq.Expressions.Expression<Func<Cliente, bool>> predicate) =>
            throw new NotSupportedException();
        public DaxQuery<Cliente> OrderBy<TKey>(System.Linq.Expressions.Expression<Func<Cliente, TKey>> keySelector) =>
            throw new NotSupportedException();
        public DaxQuery<Cliente> OrderByDescending<TKey>(System.Linq.Expressions.Expression<Func<Cliente, TKey>> keySelector) =>
            throw new NotSupportedException();
        public DaxQuery<Cliente> Take(int count) => throw new NotSupportedException();
        public DaxQuery<Cliente> Skip(int count) => throw new NotSupportedException();
        public DaxQuery<TResult> Select<TResult>(System.Linq.Expressions.Expression<Func<Cliente, TResult>> selector)
            where TResult : class => throw new NotSupportedException();
        public DaxGroupedQuery<Cliente, TKey> GroupBy<TKey>(System.Linq.Expressions.Expression<Func<Cliente, TKey>> keySelector) =>
            throw new NotSupportedException();
        public DaxQuery<TResult> Aggregate<TResult>(System.Linq.Expressions.Expression<Func<IDaxAggregate<Cliente>, TResult>> selector)
            where TResult : class => throw new NotSupportedException();
        public DaxQuery<Cliente> Distinct() => throw new NotSupportedException();
        public Task<List<TValue>> ValuesAsync<TValue>(System.Linq.Expressions.Expression<Func<Cliente, TValue>> selector, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<List<TValue>> DistinctValuesAsync<TValue>(System.Linq.Expressions.Expression<Func<Cliente, TValue>> selector, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<List<Cliente>> ToListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<DaxPage<Cliente>> ToPagedListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<Cliente> AsAsyncEnumerable(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<TValue?> MeasureAsync<TValue>(
            string measure, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Cliente[]> ToArrayAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Dictionary<TKey, Cliente>> ToDictionaryAsync<TKey>(Func<Cliente, TKey> keySelector, CancellationToken cancellationToken = default)
            where TKey : notnull => throw new NotSupportedException();
        public Task<Dictionary<TKey, TValue>> ToDictionaryAsync<TKey, TValue>(Func<Cliente, TKey> keySelector, Func<Cliente, TValue> valueSelector, CancellationToken cancellationToken = default)
            where TKey : notnull => throw new NotSupportedException();
        public Task<Cliente?> FirstOrDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Cliente> FirstAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Cliente> SingleAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Cliente?> SingleOrDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<long> LongCountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<TValue> SumAsync<TValue>(System.Linq.Expressions.Expression<Func<Cliente, TValue>> selector, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<TValue> MinAsync<TValue>(System.Linq.Expressions.Expression<Func<Cliente, TValue>> selector, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<TValue> MaxAsync<TValue>(System.Linq.Expressions.Expression<Func<Cliente, TValue>> selector, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<double> AverageAsync<TValue>(System.Linq.Expressions.Expression<Func<Cliente, TValue>> selector, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<double?> AverageOrDefaultAsync<TValue>(System.Linq.Expressions.Expression<Func<Cliente, TValue>> selector, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> AnyAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> AnyAsync(System.Linq.Expressions.Expression<Func<Cliente, bool>> predicate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> AllAsync(System.Linq.Expressions.Expression<Func<Cliente, bool>> predicate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public DaxQuery<TResult> Join<TInner, TKey, TResult>(
            DaxQuery<TInner> inner,
            System.Linq.Expressions.Expression<Func<Cliente, TKey>> outerKeySelector,
            System.Linq.Expressions.Expression<Func<TInner, TKey>> innerKeySelector,
            System.Linq.Expressions.Expression<Func<Cliente, TInner, TResult>> resultSelector)
            where TInner : class where TResult : class => throw new NotSupportedException();
        public DaxQuery<TResult> Join<TInner, TKey, TResult>(
            IDaxTable<TInner> inner,
            System.Linq.Expressions.Expression<Func<Cliente, TKey>> outerKeySelector,
            System.Linq.Expressions.Expression<Func<TInner, TKey>> innerKeySelector,
            System.Linq.Expressions.Expression<Func<Cliente, TInner, TResult>> resultSelector)
            where TInner : class where TResult : class => throw new NotSupportedException();
    }
}
