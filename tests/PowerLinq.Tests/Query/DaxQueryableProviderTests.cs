using System.Linq.Expressions;
using System.Text.RegularExpressions;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Linq;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// <c>IQueryable</c> as an <b>additional</b> surface: real LINQ translating into the same pipeline
/// the fluent API composes.
/// </summary>
/// <remarks>
/// <para>
/// What these tests have to prove is not that the operators work — the rest of the suite already
/// covers that through the fluent API. It is that both surfaces arrive at the <b>same</b> DAX,
/// because that is what it means for the fluent API to have become a layer over the same engine:
/// if they diverged, they would be two translators, and the second would age in silence.
/// </para>
/// <para>
/// The other half is the refusal. With <c>IQueryable</c> the fluent API's guarantee is lost — what
/// DAX cannot express does not compile — so each untranslatable thing has to blow up during
/// <b>composition</b>, naming what is written in the caller's code.
/// </para>
/// </remarks>
public sealed class DaxQueryableProviderTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Preco]")] public decimal Preco { get; set; }
        [DaxColumn("Produto[Ativo]")] public bool Ativo { get; set; }
    }

    private sealed class Resumo
    {
        public string Nome { get; set; } = "";
        public decimal Preco { get; set; }
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

    private static IQueryable<Produto> Queryable(IDaxQueryExecutor? executor = null) =>
        Table(executor).AsQueryable();

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    // ---------- the two surfaces, the same DAX ----------

    [Fact]
    public void Where_EmitsTheSameDaxAsTheFluentApi()
    {
        string queryable = Flat(Queryable().Where(p => p.Ativo && p.Preco > 100m).ToDaxString());
        string fluent = Flat(Table().Where(p => p.Ativo && p.Preco > 100m).ToDaxString());

        Assert.Equal(fluent, queryable);

        // The two sets become two stages and are merged when writing, as with the fluent API: it
        // is the DaxPipelineBuilder that joins consecutive filters over the same table.
        Assert.Equal(
            "EVALUATE FILTER(Produto, Produto[Ativo] && Produto[Preco] > 100)", queryable);
    }

    /// <remarks>
    /// The whole chain, in the order a report would write it. It is the test that would break if
    /// the provider derived the operator order instead of preserving it — each call becomes a stage
    /// at the position it appeared in, exactly as through the fluent API.
    /// </remarks>
    [Fact]
    public void ComposedChain_EmitsTheSameDaxAsTheFluentApi()
    {
        string queryable = Flat(Queryable()
            .Where(p => p.Ativo)
            .OrderByDescending(p => p.Preco)
            .ThenBy(p => p.Nome)
            .Skip(10)
            .Take(5)
            .ToDaxString());

        string fluent = Flat(Table()
            .Where(p => p.Ativo)
            .OrderByDescending(p => p.Preco)
            .ThenBy(p => p.Nome)
            .Skip(10)
            .Take(5)
            .ToDaxString());

        Assert.Equal(fluent, queryable);
    }

    /// <remarks>
    /// After <c>Take</c> comes <c>Where</c>: the order is data, and the pipeline preserves it
    /// coming from either surface.
    /// </remarks>
    [Fact]
    public void WhereAfterTake_FiltersTheWindow()
    {
        string dax = Flat(Queryable().Take(5).Where(p => p.Ativo).ToDaxString());

        Assert.Equal("EVALUATE FILTER(TOPN(5, Produto), Produto[Ativo])", dax);
    }

    [Fact]
    public void Select_ProjectsIntoTheContract()
    {
        string dax = Flat(Queryable()
            .Where(p => p.Ativo)
            .Select(p => new Resumo { Nome = p.Nome, Preco = p.Preco })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SELECTCOLUMNS(FILTER(Produto, Produto[Ativo]), \"Nome\", Produto[Nome], \"Preco\", Produto[Preco])",
            dax);
    }

    [Fact]
    public void Distinct_AfterProjection_DeduplicatesOnTheServer()
    {
        string dax = Flat(Queryable()
            .Select(p => new Resumo { Nome = p.Nome, Preco = p.Preco })
            .Distinct()
            .ToDaxString());

        Assert.StartsWith("EVALUATE DISTINCT(SELECTCOLUMNS(Produto,", dax);
    }

    // ---------- query syntax ----------

    /// <remarks>
    /// It is what <b>only</b> <c>IQueryable</c> delivers, and the reason the surface exists even
    /// after the internal structure was sorted out: <c>from ... where ... orderby ... select</c>
    /// does not compile over a bespoke fluent API, however complete it is.
    /// </remarks>
    [Fact]
    public void QuerySyntax_TranslatesTheWholeChain()
    {
        IQueryable<Produto> produtos = Queryable();

        IQueryable<Resumo> consulta =
            from produto in produtos
            where produto.Ativo && produto.Preco > 50m
            orderby produto.Preco descending
            select new Resumo { Nome = produto.Nome, Preco = produto.Preco };

        string dax = Flat(consulta.ToDaxString());

        Assert.StartsWith(
            "EVALUATE SELECTCOLUMNS(FILTER(Produto, Produto[Ativo] && Produto[Preco] > 50),",
            dax);

        // The ordering is rewritten onto the projected column, as with the fluent API:
        // Produto[Preco] does not exist in the SELECTCOLUMNS result, and [Preco] does.
        Assert.EndsWith("ORDER BY [Preco] DESC", dax);
    }

    /// <remarks>
    /// <c>orderby a, b</c> becomes <c>OrderBy</c> followed by <c>ThenBy</c>, and the compiler only
    /// accepts the second over an <c>IOrderedQueryable</c> — which is what the provider's return
    /// type has to sustain.
    /// </remarks>
    [Fact]
    public void QuerySyntax_WithTwoOrderTerms_KeepsBoth()
    {
        IQueryable<Produto> produtos = Queryable();

        IQueryable<Produto> consulta =
            from produto in produtos
            orderby produto.Preco descending, produto.Nome
            select produto;

        Assert.EndsWith(
            "ORDER BY Produto[Preco] DESC, Produto[Nome] ASC", Flat(consulta.ToDaxString()));
    }

    // ---------- interoperability ----------

    /// <remarks>
    /// The central use case: a function that only knows <c>IQueryable&lt;T&gt;</c> — generic
    /// pagination, dynamic filtering, OData — composes over the query while knowing nothing about
    /// DAX, and the result is still translated on the server.
    /// </remarks>
    [Fact]
    public void GenericPagination_ComposesOverAnyQueryable()
    {
        static IQueryable<T> Page<T>(IQueryable<T> source, int page, int size) =>
            source.Skip((page - 1) * size).Take(size);

        string dax = Flat(Page(Queryable().OrderBy(p => p.Id), page: 3, size: 20).ToDaxString());

        Assert.Contains("TOPN(20,", dax, StringComparison.Ordinal);
        Assert.Contains("TOPN(40,", dax, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The bridge in the other direction: filter with the API that fails at compile time and only
    /// then hand the query to whoever expects an <c>IQueryable</c>. What crosses over is the
    /// pipeline, so the earlier filter is not lost.
    /// </remarks>
    [Fact]
    public void FluentQuery_CrossesOverToQueryable_KeepingWhatWasComposed()
    {
        IQueryable<Produto> queryable = Table().Where(p => p.Ativo).AsQueryable();

        string dax = Flat(queryable.Take(3).ToDaxString());

        Assert.Equal("EVALUATE TOPN(3, FILTER(Produto, Produto[Ativo]))", dax);
    }

    /// <remarks>
    /// Third-party libraries build the tree by reflection and call the <b>non</b>-generic form of
    /// <c>CreateQuery</c>. It exists for that, and has to return the translated query — and not
    /// wrap the refusal in a <c>TargetInvocationException</c> when something does not translate.
    /// </remarks>
    [Fact]
    public void NonGenericCreateQuery_TranslatesLikeTheGenericOne()
    {
        IQueryable<Produto> source = Queryable();
        IQueryProvider provider = source.Provider;

        Expression call = Expression.Call(
            typeof(System.Linq.Queryable),
            nameof(System.Linq.Queryable.Take),
            [typeof(Produto)],
            source.Expression,
            Expression.Constant(7));

        IQueryable created = provider.CreateQuery(call);

        Assert.Equal(typeof(Produto), created.ElementType);
        Assert.Equal("EVALUATE TOPN(7, Produto)", Flat(((IQueryable<Produto>)created).ToDaxString()));
    }

    /// <remarks>
    /// The same non-generic form, now with an untranslatable operator. What is checked is the
    /// exception that <b>arrives</b>: invoking by reflection wraps everything in a
    /// <c>TargetInvocationException</c>, and the message that matters would vanish from the top of the stack.
    /// </remarks>
    [Fact]
    public void NonGenericCreateQuery_UnwrapsTheRefusal()
    {
        IQueryable<Produto> source = Queryable();

        Expression call = Expression.Call(
            typeof(System.Linq.Queryable),
            nameof(System.Linq.Queryable.Reverse),
            [typeof(Produto)],
            source.Expression);

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => source.Provider.CreateQuery(call));

        Assert.Contains("Reverse", error.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// An expression that is neither an operator call nor the source's constant can neither start
    /// nor continue the query — it is what a hand-built tree gets wrong first.
    /// </remarks>
    [Fact]
    public void ExpressionThatIsNotAnOperatorChain_IsRefused()
    {
        IQueryProvider provider = Queryable().Provider;

        Assert.Throws<NotSupportedException>(
            () => provider.CreateQuery<Produto>(Expression.Constant(new List<Produto>().AsQueryable())));

        Assert.Throws<NotSupportedException>(
            () => provider.CreateQuery(Expression.Constant(42)));
    }

    /// <remarks>
    /// The page size is almost never a literal: it comes from a parameter, and the compiler puts it
    /// into the tree as a field of a capture class. Refusing that would leave pagination out.
    /// </remarks>
    [Fact]
    public void Window_AcceptsACapturedVariable()
    {
        int tamanho = 25;

        Assert.Equal(
            "EVALUATE TOPN(25, Produto)", Flat(Queryable().Take(tamanho).ToDaxString()));
    }

    [Fact]
    public void Provider_BuiltDirectly_QueriesTheMappedTable()
    {
        IQueryable<Produto> produtos = new DaxQueryProvider(new RecordingExecutor()).Root<Produto>();

        Assert.Equal("EVALUATE Produto", Flat(produtos.ToDaxString()));
    }

    // ---------- the terminals ----------

    [Fact]
    public async Task ToListAsync_SendsTheComposedQuery()
    {
        var executor = new RecordingExecutor(rows: 2);

        List<Produto> produtos = await Queryable(executor).Where(p => p.Ativo).ToListAsync();

        Assert.Equal(2, produtos.Count);
        Assert.Equal("EVALUATE FILTER(Produto, Produto[Ativo])", Flat(executor.LastQuery!));
    }

    [Fact]
    public async Task CountAsync_CountsOnTheServer()
    {
        var executor = new RecordingExecutor(rows: 42);

        Assert.Equal(42, await Queryable(executor).Where(p => p.Ativo).CountAsync());
        Assert.Contains("COUNTROWS", executor.LastQuery!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SumAsync_IteratesTheColumnOnTheServer()
    {
        var executor = new RecordingExecutor(rows: 7);

        Assert.Equal(7m, await Queryable(executor).SumAsync(p => p.Preco));
        Assert.Contains("SUMX", executor.LastQuery!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_WithPredicate_AppliesItBeforeTheWindow()
    {
        var executor = new RecordingExecutor(rows: 1);

        await Queryable(executor).FirstOrDefaultAsync(p => p.Ativo);

        Assert.Equal("EVALUATE TOPN(1, FILTER(Produto, Produto[Ativo]))", Flat(executor.LastQuery!));
    }

    [Fact]
    public async Task AnyAsync_AsksTheServerWithoutFetchingRows()
    {
        var executor = new RecordingExecutor(rows: 3);

        Assert.True(await Queryable(executor).AnyAsync(p => p.Ativo));
        Assert.Contains("COUNTROWS", executor.LastQuery!, StringComparison.Ordinal);
    }

    // ---------- the refusals ----------

    /// <remarks>
    /// The rule is to fail during <b>composition</b>, not during execution. With no terminal at
    /// all, the untranslatable operator already blows up — with the stack pointing at the line it
    /// was written on, instead of a <c>ToListAsync</c> that lost the context.
    /// </remarks>
    [Fact]
    public void MemberWithoutTranslation_ThrowsAtComposition_NamingTheMember()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Queryable().Where(p => Regex.IsMatch(p.Nome, "^A")));

        Assert.Contains("IsMatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorWithoutTranslation_ThrowsNamingTheOperator()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Queryable().Reverse().ToDaxString());

        Assert.Contains("Reverse", error.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The indexed overload compiles and has no translation: in DAX a table expression is a set,
    /// with no per-row position. Translating while ignoring the index would generate a query that
    /// answers a different question.
    /// </remarks>
    [Fact]
    public void WhereWithRowIndex_IsRefusedNamingTheReason()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Queryable().Where((p, index) => index < 5 && p.Ativo));

        Assert.Contains("index", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// The overload has a refusal of its own because the general message lists the supported
    /// operators — and seeing <c>Take</c> in the list right below "Take has no translation" would
    /// make the reader doubt what is written.
    /// </remarks>
    [Fact]
    public void OverloadOfATranslatedOperator_IsRefusedAsAnOverload()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Queryable().Take(2..5));

        Assert.Contains("overload of 'Take'", error.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <c>select p.Nome</c> would return an <c>IQueryable&lt;string&gt;</c>, and materialization
    /// builds objects. The message names the terminals that read a bare column, instead of leaving
    /// the error to the server.
    /// </remarks>
    [Fact]
    public void ScalarProjection_IsRefusedPointingToValuesAsync()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Queryable().Select(p => p.Nome));

        Assert.Contains("ValuesAsync", error.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// This surface's central decision: there is <b>no</b> synchronous enumeration. Serving a
    /// <c>foreach</c> would require blocking the thread on a network round trip, and it is the kind
    /// of trap that shows up far from the cause — under load, as a starved thread pool.
    /// </remarks>
    [Fact]
    public void SynchronousEnumeration_IsRefusedPointingToTheAsyncTerminals()
    {
        IQueryable<Produto> produtos = Queryable();

        NotSupportedException fromForeach = Assert.Throws<NotSupportedException>(() =>
        {
            foreach (Produto _ in produtos)
                break;
        });

        NotSupportedException fromToList = Assert.Throws<NotSupportedException>(
            () => produtos.AsEnumerable().ToList());

        Assert.Contains("ToListAsync", fromForeach.Message, StringComparison.Ordinal);
        Assert.Contains("AsAsyncEnumerable", fromToList.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal holds on <b>both</b> <c>Execute</c> overloads. The non-generic one is what
    /// <c>IQueryProvider</c> exposes to whoever builds the call by reflection — a dynamic filtering
    /// library, for example — and leaving it out would give synchronous execution through a side
    /// door.
    /// </summary>
    [Fact]
    public void BothExecuteOverloads_RefuseSynchronousExecution()
    {
        IQueryable<Produto> produtos = Queryable();

        Assert.Throws<NotSupportedException>(
            () => produtos.Provider.Execute(produtos.Expression));

        Assert.Throws<NotSupportedException>(
            () => produtos.Provider.Execute<List<Produto>>(produtos.Expression));
    }

    /// <summary>
    /// <c>ThenByDescending</c> is the fourth ordering operator, and the only one combining "does
    /// not reset the order" with "descending" — the two dimensions swapped would produce DAX that
    /// sorts by the wrong column with no error at all.
    /// </summary>
    [Fact]
    public void ThenByDescending_EmitsTheSameDaxAsTheFluentApi()
    {
        string queryable = Flat(Queryable()
            .OrderBy(p => p.Preco)
            .ThenByDescending(p => p.Nome)
            .ToDaxString());

        string fluent = Flat(Table()
            .OrderBy(p => p.Preco)
            .ThenByDescending(p => p.Nome)
            .ToDaxString());

        Assert.Equal(fluent, queryable);
        Assert.Contains("Produto[Nome] DESC", queryable, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <c>Count()</c> and <c>CountAsync()</c> differ by a suffix: the message names the right
    /// method instead of leaving the caller hunting for it.
    /// </remarks>
    [Fact]
    public void SynchronousTerminal_NamesItsAsyncCounterpart()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => Queryable().Count());

        Assert.Contains("CountAsync", error.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// No implicit client-side evaluation: an operator DAX cannot express does not fall into memory
    /// by omission, nor partway through the chain. The boundary has a name and is called deliberately.
    /// </remarks>
    [Fact]
    public async Task ClientEvaluation_HappensOnlyAfterAnExplicitBoundary()
    {
        var executor = new RecordingExecutor(rows: 3);

        Assert.Throws<NotSupportedException>(
            () => Queryable(executor).Where(p => Regex.IsMatch(p.Nome, "^A")).ToDaxString());

        List<Produto> emMemoria = await Queryable(executor).Where(p => p.Ativo).ToListAsync();

        Assert.Equal(3, emMemoria.Count(p => Regex.IsMatch(p.Nome, "^$")));
        Assert.Equal("EVALUATE FILTER(Produto, Produto[Ativo])", Flat(executor.LastQuery!));
    }

    [Fact]
    public void ForeignQueryable_IsRefusedByTheTerminals()
    {
        IQueryable<Produto> foreign = new List<Produto>().AsQueryable();

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => foreign.ToDaxString());

        Assert.Contains("PowerLinq", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AsQueryable_OnATableThatIsNotTheConcreteOne_IsRefused()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => new StubTable().AsQueryable());

        Assert.Contains("IDaxTableFactory", error.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The refusal is a visible message, so it follows the catalog — like every public message of
    /// the library.
    /// </remarks>
    [Fact]
    public void Refusal_IsLocalized()
    {
        IDaxTable<Produto> tabela = new DaxTableFactory(new ResourceManagerPowerLinqLocalizer("pt-BR"))
            .Create<Produto>(new RecordingExecutor());

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => tabela.AsQueryable().Reverse().ToDaxString());

        Assert.Contains("não tem tradução", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An implementation of <see cref="IDaxTable{T}"/> that is not the concrete one.</summary>
    private sealed class StubTable : IDaxTable<Produto>
    {
        public DaxQuery<Produto> Where(Expression<Func<Produto, bool>> predicate) => throw new NotSupportedException();
        public DaxQuery<Produto> WhereIf(bool condition, Expression<Func<Produto, bool>> predicate) => throw new NotSupportedException();
        public DaxQuery<Produto> WhereIf(string? value, Expression<Func<Produto, bool>> predicate) => throw new NotSupportedException();
        public DaxQuery<Produto> OrderBy<TKey>(Expression<Func<Produto, TKey>> keySelector) => throw new NotSupportedException();
        public DaxQuery<Produto> OrderByDescending<TKey>(Expression<Func<Produto, TKey>> keySelector) => throw new NotSupportedException();
        public DaxQuery<Produto> Take(int count) => throw new NotSupportedException();
        public DaxQuery<Produto> Skip(int count) => throw new NotSupportedException();
        public DaxQuery<TResult> Select<TResult>(Expression<Func<Produto, TResult>> selector) where TResult : class => throw new NotSupportedException();
        public DaxGroupedQuery<Produto, TKey> GroupBy<TKey>(Expression<Func<Produto, TKey>> keySelector) => throw new NotSupportedException();
        public DaxQuery<TResult> Aggregate<TResult>(Expression<Func<IDaxAggregate<Produto>, TResult>> selector) where TResult : class => throw new NotSupportedException();
        public DaxQuery<Produto> Distinct() => throw new NotSupportedException();
        public Task<List<TValue>> DistinctValuesAsync<TValue>(Expression<Func<Produto, TValue>> selector, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<TValue>> ValuesAsync<TValue>(Expression<Func<Produto, TValue>> selector, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<Produto>> ToListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<Produto> AsAsyncEnumerable(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DaxPage<Produto>> ToPagedListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TValue?> MeasureAsync<TValue>(string measure, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Produto?> FirstOrDefaultAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Produto> FirstAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Produto[]> ToArrayAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Dictionary<TKey, Produto>> ToDictionaryAsync<TKey>(Func<Produto, TKey> keySelector, CancellationToken cancellationToken = default) where TKey : notnull => throw new NotSupportedException();
        public Task<Dictionary<TKey, TValue>> ToDictionaryAsync<TKey, TValue>(Func<Produto, TKey> keySelector, Func<Produto, TValue> valueSelector, CancellationToken cancellationToken = default) where TKey : notnull => throw new NotSupportedException();
        public Task<Produto> SingleAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Produto?> SingleOrDefaultAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> LongCountAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TValue> SumAsync<TValue>(Expression<Func<Produto, TValue>> selector, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TValue> MinAsync<TValue>(Expression<Func<Produto, TValue>> selector, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TValue> MaxAsync<TValue>(Expression<Func<Produto, TValue>> selector, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<double> AverageAsync<TValue>(Expression<Func<Produto, TValue>> selector, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<double?> AverageOrDefaultAsync<TValue>(Expression<Func<Produto, TValue>> selector, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> AnyAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> AnyAsync(Expression<Func<Produto, bool>> predicate, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> AllAsync(Expression<Func<Produto, bool>> predicate, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public DaxQuery<TResult> Join<TInner, TKey, TResult>(DaxQuery<TInner> inner, Expression<Func<Produto, TKey>> outerKeySelector, Expression<Func<TInner, TKey>> innerKeySelector, Expression<Func<Produto, TInner, TResult>> resultSelector) where TInner : class where TResult : class => throw new NotSupportedException();
        public DaxQuery<TResult> Join<TInner, TKey, TResult>(IDaxTable<TInner> inner, Expression<Func<Produto, TKey>> outerKeySelector, Expression<Func<TInner, TKey>> innerKeySelector, Expression<Func<Produto, TInner, TResult>> resultSelector) where TInner : class where TResult : class => throw new NotSupportedException();
    }
}
