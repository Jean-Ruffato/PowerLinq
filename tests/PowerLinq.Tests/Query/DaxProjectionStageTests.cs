using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// The projection stopped being a terminal class and became a pipeline <b>stage</b>, so
/// <c>Select</c> returns a <see cref="DaxQuery{TResult}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The gain is measured in operators: <c>DaxProjectionQuery</c> had <c>ToListAsync</c> and
/// <c>FirstOrDefaultAsync</c>. After a <c>Select</c> the following now apply too:
/// <c>ToArrayAsync</c>, <c>ToDictionaryAsync</c>, <c>FirstAsync</c>, <c>SingleAsync</c>,
/// <c>SingleOrDefaultAsync</c>, <c>CountAsync</c>, <c>LongCountAsync</c> and <c>AnyAsync</c> —
/// none of them written again, all inherited because the projection is a stage like any other.
/// </para>
/// <para>
/// What does <b>not</b> apply is an operator that resolves columns. After a <c>SELECTCOLUMNS</c>
/// the result's columns are <c>[Nome]</c>, and resolution still uses the source entity's mapping —
/// it would emit <c>Produto[Nome]</c>, which the projected result does not have. Refusing is
/// preferable to emitting the wrong reference; resolving over the projected one is left to the reshape.
/// </para>
/// </remarks>
public sealed class DaxProjectionStageTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Preco]")] public decimal Preco { get; set; }
        [DaxColumn("Produto[Ativo]")] public bool Ativo { get; set; }
    }

    /// <summary>No <c>[DaxColumn]</c>: the output name comes from the property's name.</summary>
    private sealed class PorConvencao
    {
        public string Nome { get; set; } = "";
        public decimal Preco { get; set; }
    }

    /// <summary>With <c>[DaxColumn("[alias]")]</c>: the brackets come off the output name.</summary>
    private sealed class PorAtributo
    {
        [DaxColumn("[Descricao]")] public string Nome { get; set; } = "";
    }

    private sealed class RecordingExecutor(int rows = 0) : IDaxQueryExecutor
    {
        public string? LastQuery { get; private set; }

        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class
        {
            LastQuery = daxQuery;
            return Task.FromResult(Enumerable.Range(0, rows).Select(_ => Activator.CreateInstance<T>()).ToList());
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

    private static DaxTable<Produto> Table(IDaxQueryExecutor? executor = null) =>
        new(executor ?? new RecordingExecutor());

    private static IDaxTable<Produto> PortugueseTable() =>
        new DaxTableFactory(new ResourceManagerPowerLinqLocalizer("pt-BR"))
            .Create<Produto>(new RecordingExecutor());

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    // ---------- the DAX ----------

    [Fact]
    public void Select_EmitsSelectColumnsOverTheSource()
    {
        string dax = Flat(Table()
            .Select(p => new PorConvencao { Nome = p.Nome, Preco = p.Preco })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SELECTCOLUMNS(Produto, \"Nome\", Produto[Nome], \"Preco\", Produto[Preco])",
            dax);
    }

    [Fact]
    public void Select_AfterWhereAndTake_ProjectsTheWindow()
    {
        string dax = Flat(Table()
            .Where(p => p.Ativo)
            .OrderByDescending(p => p.Preco)
            .Take(3)
            .Select(p => new PorConvencao { Nome = p.Nome, Preco = p.Preco })
            .ToDaxString());

        Assert.StartsWith(
            "EVALUATE SELECTCOLUMNS(TOPN(3, FILTER(Produto, Produto[Ativo]), Produto[Preco], DESC),",
            dax);

        // The earlier ordering is REWRITTEN onto the projected column: Produto[Preco] does not
        // exist in the SELECTCOLUMNS result, but [Preco] does.
        Assert.EndsWith("ORDER BY [Preco] DESC", dax);
    }

    /// <summary>
    /// Ordering by a column the projection does not carry forward is refused. Before this check,
    /// the <c>ORDER BY</c> came out with the source table's reference — a column the
    /// <c>SELECTCOLUMNS</c> does not return, and which the server rejects.
    /// </summary>
    [Fact]
    public void Select_DroppingTheOrderingColumn_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table()
                .OrderByDescending(p => p.Preco)
                .Select(p => new PorAtributo { Nome = p.Nome })
                .ToDaxString());

        Assert.Contains("Produto[Preco]", ex.Message);
        Assert.Contains("Select", ex.Message);
    }

    [Fact]
    public void Select_WithColumnAttribute_UsesTheAliasWithoutBrackets()
    {
        string dax = Flat(Table().Select(p => new PorAtributo { Nome = p.Nome }).ToDaxString());

        Assert.Equal("EVALUATE SELECTCOLUMNS(Produto, \"Descricao\", Produto[Nome])", dax);
    }

    [Fact]
    public void Select_AcceptsComputedExpressions()
    {
        string dax = Flat(Table()
            .Select(p => new PorConvencao { Nome = p.Nome, Preco = p.Preco * 1.1m })
            .ToDaxString());

        Assert.Contains("Produto[Preco] * 1.1", dax);
    }

    // ---------- the operators the projection came to inherit ----------

    [Fact]
    public async Task CountAsync_AfterSelect_CountsTheProjection()
    {
        var executor = new RecordingExecutor(rows: 4);

        int total = await Table(executor)
            .Select(p => new PorConvencao { Nome = p.Nome })
            .CountAsync();

        Assert.Equal(4, total);
        Assert.StartsWith(
            "EVALUATE ROW(\"[Count]\", COUNTROWS(SELECTCOLUMNS(Produto,",
            Flat(executor.LastQuery!));
    }

    [Fact]
    public async Task ToArrayAsync_AfterSelect_Materializes()
    {
        PorConvencao[] linhas = await Table(new RecordingExecutor(rows: 3))
            .Select(p => new PorConvencao { Nome = p.Nome })
            .ToArrayAsync();

        Assert.Equal(3, linhas.Length);
    }

    [Fact]
    public async Task SingleAsync_AfterSelect_AsksForTwoRows()
    {
        var executor = new RecordingExecutor(rows: 1);

        await Table(executor).Select(p => new PorConvencao { Nome = p.Nome }).SingleAsync();

        Assert.StartsWith("EVALUATE TOPN(2, SELECTCOLUMNS(Produto,", Flat(executor.LastQuery!));
    }

    [Fact]
    public async Task SingleAsync_AfterSelect_OverTwoRows_Throws() =>
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Table(new RecordingExecutor(rows: 2))
                .Select(p => new PorConvencao { Nome = p.Nome })
                .SingleAsync());

    [Fact]
    public async Task FirstAsync_AfterSelect_NarrowsToOneRow()
    {
        var executor = new RecordingExecutor(rows: 1);

        await Table(executor).Select(p => new PorConvencao { Nome = p.Nome }).FirstAsync();

        Assert.StartsWith("EVALUATE TOPN(1, SELECTCOLUMNS(Produto,", Flat(executor.LastQuery!));
    }

    [Fact]
    public async Task ToDictionaryAsync_AfterSelect_IndexesClientSide()
    {
        int i = 0;

        Dictionary<int, PorConvencao> porIndice = await Table(new RecordingExecutor(rows: 2))
            .Select(p => new PorConvencao { Nome = p.Nome })
            .ToDictionaryAsync(_ => i++);

        Assert.Equal(2, porIndice.Count);
    }

    // ---------- what the projection refuses, and why ----------

    [Theory]
    [InlineData("SumAsync")]
    [InlineData("GroupBy")]
    [InlineData("Aggregate")]
    public void OperatorsThatResolveAColumn_AreRefusedAfterSelect(string @operator)
    {
        DaxQuery<PorConvencao> projetada = Table().Select(p => new PorConvencao { Nome = p.Nome });

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
        {
            switch (@operator)
            {
                case "SumAsync":
                    projetada.SumAsync(r => r.Preco).GetAwaiter().GetResult();
                    break;
                case "GroupBy":
                    projetada.GroupBy(r => r.Nome);
                    break;
                default:
                    projetada.Aggregate(g => new PorConvencao { Preco = g.Sum(r => r.Preco) });
                    break;
            }
        });

        Assert.Contains("after Select", ex.Message);
        // The message says what still works, so the projection does not look like a dead end.
        Assert.Contains("CountAsync", ex.Message);
    }

    [Fact]
    public void TheRefusal_IsLocalized()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => PortugueseTable().Select(p => new PorConvencao { Nome = p.Nome })
                                   .GroupBy(r => r.Nome));

        Assert.StartsWith("GroupBy não pode ser aplicado depois de Select", ex.Message);
    }

    /// <summary>
    /// <c>Where</c> after <c>Select</c> started working: the predicate is resolved against the
    /// result's columns, so the reference comes out as <c>[Nome]</c> — and not
    /// <c>Produto[Nome]</c>, which the <c>SELECTCOLUMNS</c> does not return.
    /// </summary>
    [Fact]
    public void Where_AfterSelect_FiltersTheProjectedColumns()
    {
        string dax = Flat(Table()
            .Select(p => new PorConvencao { Nome = p.Nome, Preco = p.Preco })
            .Where(r => r.Preco > 100m)
            .ToDaxString());

        Assert.Equal(
            "EVALUATE FILTER(SELECTCOLUMNS(Produto, \"Nome\", Produto[Nome], \"Preco\", Produto[Preco]), "
                + "[Preco] > 100)",
            dax);

        // The distinction that matters: the predicate's reference is NOT the source table's.
        Assert.DoesNotContain("), Produto[Preco] > 100)", dax, StringComparison.Ordinal);
    }

    [Fact]
    public void Where_AfterSelect_ByAColumnTheProjectionDropped_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table()
                .Select(p => new PorConvencao { Nome = p.Nome })
                .Where(r => r.Preco > 100m));

        // Preco was not projected, so it is not in the result's closed set. The message names what
        // is.
        Assert.Contains("PorConvencao.Preco", ex.Message);
        Assert.Contains("[Nome]", ex.Message);
    }

    /// <summary>
    /// <c>AnyAsync</c> without a predicate resolves no column, so it works; with a predicate it
    /// composes a <c>Where</c> and is refused for the same reason it is.
    /// </summary>
    [Fact]
    public async Task AnyAsync_WithoutPredicate_WorksAfterSelect()
    {
        bool existe = await Table(new RecordingExecutor(rows: 1))
            .Select(p => new PorConvencao { Nome = p.Nome })
            .AnyAsync();

        Assert.True(existe);
    }

    [Fact]
    public void Select_WithoutObjectInitializer_StillThrows()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table().Select(p => new PorConvencao()));

        Assert.StartsWith("Select must use an object initializer", ex.Message);
    }

    /// <summary>
    /// A body that is neither an initializer nor a construction — here the entity itself — falls
    /// into the same refusal. There is no column to name in the <c>SELECTCOLUMNS</c>, and emitting
    /// the whole table would be returning a different query than the one that was written.
    /// </summary>
    [Fact]
    public void Select_OfTheEntityItself_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table().Select(p => p));

        Assert.StartsWith("Select must use an object initializer", ex.Message);
    }

    private sealed class ComCampo
    {
#pragma warning disable CA1051 // O campo público é o ponto do teste: só propriedade é projetável.
        public string Nome = "";
#pragma warning restore CA1051
    }

    /// <summary>
    /// Only a <b>property</b> is projectable. Assigning a field in the initializer is refused
    /// rather than ignored: ignoring it would produce a <c>SELECTCOLUMNS</c> without the column
    /// that was asked for, and the absence would only show up as a default value at materialization.
    /// </summary>
    [Fact]
    public void Select_AssigningAField_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table().Select(p => new ComCampo { Nome = p.Nome }));

        Assert.Contains("propert", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
