using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Builders;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.Tests.Query;

/// <summary>
/// The source is declared once in the <c>DEFINE</c> block when more than one place references it.
/// </summary>
/// <remarks>
/// <para>
/// The generator is pagination's: <c>Skip</c> emits <c>EXCEPT(source, TOPN(n, source, ...))</c>,
/// with the whole subtree on both sides. Over a large <c>FILTER</c> or <c>SUMMARIZECOLUMNS</c> that
/// is written twice and evaluated twice — and each chained <c>Skip</c> multiplies it again. The
/// hand-written DAX the library is meant to replace uses <c>VAR</c> in exactly this spot.
/// </para>
/// <para>
/// The semantics do not change: a query <c>VAR</c> is evaluated once and reused. That is why the
/// tests pin down the <b>text</b> — it is the only thing the change alters.
/// </para>
/// </remarks>
public sealed class DaxQueryVariableTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("Produto[Ativo]")] public bool Ativo { get; set; }
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

    private static DaxTable<Produto> Table() => new(new NoopExecutor());

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string fragment) =>
        text.Split(fragment).Length - 1;

    [Fact]
    public void SkipOverAFilter_DeclaresTheSourceOnce()
    {
        string dax = Flat(Table().Where(p => p.Ativo).OrderBy(p => p.Id).Skip(10).ToDaxString());

        Assert.Equal(
            "DEFINE VAR __pl_source_0 = FILTER(Produto, Produto[Ativo]) "
            + "EVALUATE EXCEPT(__pl_source_0, TOPN(10, __pl_source_0, Produto[Id], ASC)) "
            + "ORDER BY Produto[Id] ASC",
            dax);

        Assert.Equal(1, Occurrences(dax, "FILTER("));
    }

    /// <summary>
    /// A raw table reference gets <b>no</b> declaration. <c>EXCEPT(Produto, TOPN(10, Produto,
    /// ...))</c> repeats no work at all — <c>Produto</c> is the name of a model table, not a subtree
    /// to evaluate — and declaring it would only add noise to every simple <c>Skip</c>.
    /// </summary>
    [Fact]
    public void SkipOverABareTable_DeclaresNothing()
    {
        string dax = Flat(Table().OrderBy(p => p.Id).Skip(10).ToDaxString());

        Assert.DoesNotContain("DEFINE", dax, StringComparison.Ordinal);
        Assert.Equal(
            "EVALUATE EXCEPT(Produto, TOPN(10, Produto, Produto[Id], ASC)) ORDER BY Produto[Id] ASC",
            dax);
    }

    /// <summary>
    /// Each chained <c>Skip</c> declares its own level. Without that the subtree doubled at each
    /// one: two <c>Skip</c>s gave three <c>EXCEPT</c>s in the text, three gave seven.
    /// </summary>
    [Fact]
    public void ChainedSkips_EachDeclareTheirLevel()
    {
        string dax = Flat(Table().Where(p => p.Ativo).OrderBy(p => p.Id)
                                 .Skip(10).Skip(5).Skip(2).ToDaxString());

        Assert.Equal(3, Occurrences(dax, "VAR __pl_source_"));
        Assert.Equal(1, Occurrences(dax, "FILTER("));

        // The names are distinct and numbered in declaration order — a VAR only sees the ones
        // declared before it, so repeating a name would silently change the meaning.
        Assert.Contains("VAR __pl_source_0 = FILTER(", dax, StringComparison.Ordinal);
        Assert.Contains("VAR __pl_source_1 = EXCEPT(__pl_source_0", dax, StringComparison.Ordinal);
        Assert.Contains("VAR __pl_source_2 = EXCEPT(__pl_source_1", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count carries the block too: <c>COUNTROWS</c> wraps the same folded source, and a
    /// <c>DEFINE</c> lost along the way would leave the DAX referencing an undeclared <c>VAR</c>.
    /// </summary>
    [Fact]
    public async Task TheCountQuery_CarriesTheDefineBlock()
    {
        string? enviado = null;

        var executor = new CapturingExecutor(dax => enviado = dax);

        _ = await new DaxTable<Produto>(executor)
            .Where(p => p.Ativo)
            .OrderBy(p => p.Id)
            .Skip(10)
            .CountAsync();

        string dax = Flat(enviado!);

        Assert.StartsWith("DEFINE VAR __pl_source_0 = FILTER(", dax, StringComparison.Ordinal);
        Assert.Contains("EVALUATE ROW(\"[Count]\", COUNTROWS(EXCEPT(__pl_source_0,", dax, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(dax, "FILTER("));
    }

    /// <summary>
    /// The scalar aggregate too — same folding function, and it wraps the result in an iterator.
    /// </summary>
    [Fact]
    public async Task TheScalarQuery_CarriesTheDefineBlock()
    {
        string? enviado = null;

        var executor = new CapturingExecutor(dax => enviado = dax);

        _ = await new DaxTable<Produto>(executor)
            .Where(p => p.Ativo)
            .OrderBy(p => p.Id)
            .Skip(10)
            .SumAsync(p => p.Id);

        string dax = Flat(enviado!);

        Assert.StartsWith("DEFINE VAR __pl_source_0 = FILTER(", dax, StringComparison.Ordinal);
        Assert.Contains("SUMX(EXCEPT(__pl_source_0,", dax, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(dax, "FILTER("));
    }

    /// <summary>
    /// A <c>Join</c>'s inner side folds through the same function, and both sides' declarations go
    /// into the <b>same</b> <c>DEFINE</c> block. Numbering per fold would give two
    /// <c>__pl_source_0</c> in the same DAX, and the second would silently overwrite the first —
    /// the query would stay valid and return the wrong page.
    /// </summary>
    /// <remarks>
    /// Built by the builder, and not by the query API: <c>DaxQuery.Join</c> today refuses any inner
    /// stage that is not a filter. But <c>DaxPipelineBuilder</c> is public, it is the same reason it
    /// revalidates the ordering against the result, and the restriction on the query side may loosen
    /// without anyone remembering this place.
    /// </remarks>
    [Fact]
    public void BothSidesOfAJoin_ShareOneDefineBlockWithDistinctNames()
    {
        var externo = new DaxFilterStage(
            new DaxBinary(DaxOperator.Equal, new DaxColumnRef("Produto[Ativo]"), DaxLiteral.From(true)));

        var ordem = new DaxOrderStage(new DaxOrderTerm(new DaxColumnRef("Venda[Id]"), true), true);

        var join = new DaxJoinStage(
            "Venda",
            [
                new DaxFilterStage(new DaxBinary(
                    DaxOperator.GreaterThan, new DaxColumnRef("Venda[Total]"), DaxLiteral.From(10m))),
                ordem,
                new DaxSkipStage(3)
            ],
            ["Produto[Id]"],
            ["Venda[ProdutoId]"],
            [
                // Id goes into the result because the pending ordering is by it: without that the
                // join would leave it out and the fold would refuse, which is the reshape's guard.
                new DaxJoinProjection("Id", true, new DaxColumnRef("Produto[Id]")),
                new DaxJoinProjection("Nome", true, new DaxColumnRef("Produto[Nome]")),
                new DaxJoinProjection("Total", false, new DaxColumnRef("Venda[Total]"))
            ]);

        var pipeline = new DaxPipeline("Produto", typeof(object))
        {
            Stages = [externo,
                      new DaxOrderStage(new DaxOrderTerm(new DaxColumnRef("Produto[Id]"), true), true),
                      new DaxSkipStage(5),
                      join]
        };

        string dax = Flat(DaxPipelineBuilder.Build(pipeline));

        Assert.Equal(2, Occurrences(dax, "VAR __pl_source_"));
        Assert.Contains("VAR __pl_source_0 = FILTER(Produto,", dax, StringComparison.Ordinal);
        Assert.Contains("VAR __pl_source_1 = FILTER(Venda,", dax, StringComparison.Ordinal);
    }

    private sealed class CapturingExecutor(Action<string> capture) : IDaxQueryExecutor
    {
        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class
        {
            capture(daxQuery);
            return Task.FromResult(new List<T>());
        }

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            capture(daxQuery);
            return Task.FromResult<object?>(null);
        }

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            capture(daxQuery);
            return Task.FromResult(0);
        }
    }
}
