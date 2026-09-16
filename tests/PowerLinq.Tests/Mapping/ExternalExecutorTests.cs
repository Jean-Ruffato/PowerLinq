using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Mapping;

namespace PowerLinq.Tests.Mapping;

/// <summary>
/// An executor from <b>outside</b> the library can use its mapper.
/// </summary>
/// <remarks>
/// <para>
/// <c>IDaxQueryExecutor</c> is the extension point: anyone who already has their own XMLA
/// transport implements the interface and reuses the whole translator. But the implementation
/// could not call <c>EntityMapper.MapResult&lt;T&gt;</c>, because the generic constraints did not
/// match — the mapper required <c>new()</c> and the interface did not declare it. Adding the
/// constraint on the implementation did not compile either (<c>CS0425</c>), so what was left was
/// reflection over a public API to do what it exists to do.
/// </para>
/// <para>
/// <c>new()</c> was relaxed to <c>class</c> across the whole chain — to let immutable contracts
/// materialize — and that fixed this <b>as a side effect</b>. This test exists because the
/// compatibility of the two constraints is invisible: nothing visibly breaks if one of them
/// diverges again, and the symptom reappears as a compile error in third-party code, outside this solution.
/// </para>
/// </remarks>
public sealed class ExternalExecutorTests
{
    private sealed class Linha
    {
        [DaxColumn("[nome]")] public string Nome { get; set; } = "";
        [DaxColumn("[valor]")] public decimal Valor { get; set; }
    }

    /// <summary>
    /// Written the way an external implementer would write it: it calls <c>MapResult&lt;T&gt;</c>
    /// directly, with no reflection and without repeating the type conversion.
    /// </summary>
    private sealed class ExecutorDeTerceiro : IDaxQueryExecutor
    {
        private readonly DaxResult _result;

        public ExecutorDeTerceiro(DaxResult result) => _result = result;

        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class =>
            // This is the line that did not compile. If the constraints diverge again, this file
            // stops compiling — which is exactly the symptom the external consumer saw.
            Task.FromResult(EntityMapper.MapResult<T>(_result));

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    [Fact]
    public async Task AnExternalExecutor_CanCallTheMapperDirectly()
    {
        var result = new DaxResult(
            ["[nome]", "[valor]"],
            [
                new DaxRow(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["[nome]"] = "Mesa",
                    ["[valor]"] = "199.90"
                })
            ]);

        List<Linha> linhas = await new ExecutorDeTerceiro(result).ExecuteAsync<Linha>("EVALUATE X");

        Assert.Single(linhas);
        Assert.Equal("Mesa", linhas[0].Nome);

        // The invariant-culture conversion comes along — it is what reimplementing the mapping
        // would lose, and the reason reflection was preferable to that.
        Assert.Equal(199.90m, linhas[0].Valor);
    }

    /// <summary>
    /// <c>MapResult</c>'s constraint is <c>class</c>, not <c>new()</c>: a contract with no
    /// parameterless constructor goes through here too.
    /// </summary>
    private sealed record LinhaImutavel(
        [property: DaxColumn("[nome]")] string Nome,
        [property: DaxColumn("[valor]")] decimal Valor);

    [Fact]
    public async Task AnExternalExecutor_AlsoMapsAnImmutableContract()
    {
        var result = new DaxResult(
            ["[nome]", "[valor]"],
            [
                new DaxRow(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["[nome]"] = "Cadeira",
                    ["[valor]"] = 89.5m
                })
            ]);

        List<LinhaImutavel> linhas =
            await new ExecutorDeTerceiro(result).ExecuteAsync<LinhaImutavel>("EVALUATE X");

        Assert.Equal("Cadeira", linhas[0].Nome);
        Assert.Equal(89.5m, linhas[0].Valor);
    }
}
