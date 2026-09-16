using BenchmarkDotNet.Attributes;
using PowerLinq.Benchmark.Model;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Benchmark.Benchmarks;

/// <summary>
/// The gain of a compiled query over composing the query on every execution — the dashboard case,
/// where the same query runs on every request with only the values changing.
/// </summary>
/// <remarks>
/// <para>
/// <c>DaxCacheOpportunityBenchmarks</c> answered <b>where</b> the cost is, separating translation,
/// folding and writing. This one answers <b>how much</b> the chosen solution gives back, and it is
/// the second half of the first criterion — "a real gain measured before fixing the architecture".
/// </para>
/// <para>
/// The baseline composes from scratch, which is what a service does today on every request. The
/// compiled query is a <c>static readonly</c> field, so the template already exists from the second
/// execution onwards — and measuring the first one along with it would dilute exactly what is meant
/// to be seen.
/// </para>
/// </remarks>
public class DaxCompiledQueryBenchmarks
{
    [Params(1, 4)]
    public int ChainLength { get; set; }

    private static readonly DaxCompiledQuery<Produto, string> OneFilter =
        DaxCompiledQuery.Create<Produto, string>(
            (table, categoria) => table.Where(p => p.Categoria == categoria));

    private static readonly DaxCompiledQuery<Produto, string> FourFilters =
        DaxCompiledQuery.Create<Produto, string>(
            (table, categoria) => table
                .Where(p => p.Categoria == categoria)
                .Where(p => p.Preco > 100m)
                .Where(p => p.Ativo)
                .Where(p => p.Nome != ""));

    private IDaxTable<Produto> _table = null!;
    private DaxCompiledQuery<Produto, string> _compiled = null!;

    [GlobalSetup]
    public void Setup()
    {
        _table = new DaxTable<Produto>(NoopExecutor.Instance);
        _compiled = ChainLength == 1 ? OneFilter : FourFilters;

        // The template is built outside the measurement: what is measured is the steady state, not
        // the first execution — and the first one is already measured by the baseline's whole path.
        _compiled.TemplateFor(_table);
    }

    [Benchmark(Baseline = true, Description = "Compor a cada execução")]
    public string ComposeEveryTime()
    {
        DaxQuery<Produto> query = _table.Where(p => p.Categoria == "Eletrônicos");

        if (ChainLength > 1)
        {
            query = query
                .Where(p => p.Preco > 100m)
                .Where(p => p.Ativo)
                .Where(p => p.Nome != "");
        }

        return query.ToDaxString();
    }

    [Benchmark(Description = "Ligar os valores no template")]
    public string BindCompiled() => _compiled.ToDaxString(_table, "Eletrônicos");
}
