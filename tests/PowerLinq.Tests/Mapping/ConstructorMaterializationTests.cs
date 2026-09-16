using System.Collections.Frozen;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Mapping;
using Interfaces = PowerLinq.DaxConverter.Interfaces;
using Queries = PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Mapping;

/// <summary>
/// Constructor materialization: positional <c>record</c>, anonymous type and immutable DTO.
/// </summary>
/// <remarks>
/// <para>
/// The rule is that <b>the parameterless constructor wins</b>. If the type has one, materialization
/// uses writable properties — the path that always existed, so no current contract changes
/// behavior. Only in its absence does the constructor with parameters come in.
/// </para>
/// <para>
/// The rule resolves the ambiguity with no heuristic. The alternative would be choosing by what
/// "fits best" with the result's columns, and then <b>the same class would change paths when the
/// query gained a column</b> — a contract that materializes by property in one place and by
/// constructor in another, with nothing in the code saying so.
/// </para>
/// </remarks>
public sealed class ConstructorMaterializationTests
{
    private static DaxRow Row(params (string Column, object? Value)[] cells) =>
        new(cells.ToDictionary(cell => cell.Column, cell => cell.Value));

    private static T Map<T>(DaxRow row) where T : class
    {
        FrozenDictionary<string, DaxColumnMapping> mappings = EntityMapper.GetColumnMappings(typeof(T));

        return EntityMapper.MapRow<T>(row, mappings);
    }

    // ---------- the constructor path ----------

    private sealed record ProdutoRecord(int Id, string Nome);

    [Fact]
    public void APositionalRecord_MaterializesThroughItsConstructor()
    {
        ProdutoRecord produto = Map<ProdutoRecord>(Row(("[Id]", 7), ("[Nome]", "Cadeira")));

        Assert.Equal(7, produto.Id);
        Assert.Equal("Cadeira", produto.Nome);
    }

    private sealed record ComAtributo(
        [property: DaxColumn("Produto[ProdutoID]")] int Id,
        [property: DaxColumn("Produto[Descricao]")] string Nome);

    /// <summary>
    /// <c>[DaxColumn]</c> on the property the <c>record</c> generates counts, and is what makes the
    /// same type map identically through both paths.
    /// </summary>
    [Fact]
    public void APositionalRecord_HonoursTheColumnAttributeOnTheGeneratedProperty()
    {
        ComAtributo produto = Map<ComAtributo>(
            Row(("Produto[ProdutoID]", 3), ("Produto[Descricao]", "Mesa")));

        Assert.Equal(3, produto.Id);
        Assert.Equal("Mesa", produto.Nome);
    }

    private sealed class Imutavel(string nome, decimal preco)
    {
        public string Nome { get; } = nome;
        public decimal Preco { get; } = preco;
    }

    [Fact]
    public void AnImmutableDto_MaterializesThroughItsConstructor()
    {
        Imutavel dto = Map<Imutavel>(Row(("[Nome]", "Mesa"), ("[Preco]", 199.9m)));

        Assert.Equal("Mesa", dto.Nome);
        Assert.Equal(199.9m, dto.Preco);
    }

    /// <summary>
    /// A column missing from the result gets the parameter's <c>default</c> — the same semantics as
    /// the property path, where the property simply is not assigned.
    /// </summary>
    [Fact]
    public void AMissingColumn_LeavesTheParameterAtItsDefault()
    {
        ProdutoRecord produto = Map<ProdutoRecord>(Row(("[Nome]", "Cadeira")));

        Assert.Equal(0, produto.Id);
        Assert.Equal("Cadeira", produto.Nome);
    }

    [Fact]
    public void ANullCell_LeavesTheParameterAtItsDefault()
    {
        ProdutoRecord produto = Map<ProdutoRecord>(Row(("[Id]", null), ("[Nome]", "Cadeira")));

        Assert.Equal(0, produto.Id);
    }

    /// <summary>The conversion is the same on both paths, so invariant culture here too.</summary>
    [Fact]
    public void TheConversion_IsTheSameAsThePropertyPath()
    {
        Imutavel dto = Map<Imutavel>(Row(("[Nome]", "Mesa"), ("[Preco]", "1234.56")));

        Assert.Equal(1234.56m, dto.Preco);
    }

    private sealed record ComAnulavel(int? Quantidade);

    [Fact]
    public void ANullableParameter_AcceptsTheValueAndTheAbsence()
    {
        Assert.Equal(5, Map<ComAnulavel>(Row(("[Quantidade]", 5))).Quantidade);
        Assert.Null(Map<ComAnulavel>(Row()).Quantidade);
    }

    // ---------- the rule: the parameterless constructor wins ----------

    private sealed record ComConstrutorVazio
    {
        [DaxColumn("[Nome]")] public string Nome { get; init; } = "";
    }

    /// <summary>
    /// A <c>record</c> with <c>{ get; init; }</c> and an empty constructor goes through the old path
    /// — <c>init</c> is a setter in IL, so property assignment works.
    /// </summary>
    [Fact]
    public void ARecordWithAParameterlessConstructor_UsesThePropertyPath()
    {
        ComConstrutorVazio produto = Map<ComConstrutorVazio>(Row(("[Nome]", "Cadeira")));

        Assert.Equal("Cadeira", produto.Nome);
    }

    private sealed class OsDoisCaminhos
    {
        public OsDoisCaminhos() { }

        public OsDoisCaminhos(string nome) => Nome = nome + " pelo construtor";

        [DaxColumn("[Nome]")] public string Nome { get; set; } = "";
    }

    /// <summary>
    /// The case the rule exists to decide: a type with <b>both</b>. The property wins, and the test
    /// proves it by the value — the constructor would mark the text.
    /// </summary>
    [Fact]
    public void WithBothAvailable_ThePropertyPathWins()
    {
        OsDoisCaminhos dto = Map<OsDoisCaminhos>(Row(("[Nome]", "Cadeira")));

        Assert.Equal("Cadeira", dto.Nome);
    }

    // ---------- the end-to-end Select ----------

    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
    }

    private sealed class Noop : Interfaces.IDaxQueryExecutor
    {
        public string? LastQuery { get; private set; }

        public Task<List<TRow>> ExecuteAsync<TRow>(string daxQuery, CancellationToken cancellationToken = default)
            where TRow : class
        {
            LastQuery = daxQuery;
            return Task.FromResult(new List<TRow>());
        }

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    /// <summary>
    /// An anonymous type in <c>Select</c>. It is not a <c>MemberInitExpression</c> — it has no
    /// writable property to initialize — so it used to fall into <c>SelectInitializerRequired</c>.
    /// </summary>
    [Fact]
    public void AnAnonymousType_Projects()
    {
        var query = new Queries.DaxTable<Produto>(new Noop())
            .Select(p => new { p.Id, p.Nome });

        Assert.Equal(
            "EVALUATE SELECTCOLUMNS(Produto, \"Id\", Produto[Id], \"Nome\", Produto[Nome])",
            Flat(query.ToDaxString()));
    }

    [Fact]
    public void APositionalRecord_Projects()
    {
        var query = new Queries.DaxTable<Produto>(new Noop())
            .Select(p => new ProdutoRecord(p.Id, p.Nome));

        // With Members not filled in, the output name comes from the constructor parameter — which
        // is the same name as the generated property, so projection and materialization agree.
        Assert.Equal(
            "EVALUATE SELECTCOLUMNS(Produto, \"Id\", Produto[Id], \"Nome\", Produto[Nome])",
            Flat(query.ToDaxString()));
    }

    /// <summary>
    /// The output name is the member's, not the source column's. In an anonymous type both arrive
    /// through the same place — the compiler names the constructor parameter after the member — so
    /// the test pins down the result, which is what matters, and not the mechanism.
    /// </summary>
    [Fact]
    public void AnAnonymousTypeWithARenamedMember_UsesTheMemberName()
    {
        var query = new Queries.DaxTable<Produto>(new Noop())
            .Select(p => new { Codigo = p.Id });

        Assert.Equal("EVALUATE SELECTCOLUMNS(Produto, \"Codigo\", Produto[Id])", Flat(query.ToDaxString()));
    }

    // ---------- what is refused ----------

    private sealed class DuasAridadesIguais
    {
        public DuasAridadesIguais(string nome) => Nome = nome;

        public DuasAridadesIguais(decimal preco) => Nome = preco.ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        public string Nome { get; }
    }

    /// <summary>
    /// An arity tie is refused: choosing silently would mean materialization depends on the order in
    /// which reflection returns the constructors.
    /// </summary>
    [Fact]
    public void TwoConstructorsOfTheSameArity_AreRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Map<DuasAridadesIguais>(Row(("[Nome]", "Cadeira"))));

        Assert.Contains("DuasAridadesIguais", ex.Message);
        Assert.Contains("same, highest number of parameters", ex.Message);
    }

    private sealed class MaiorAridadeGanha
    {
        public MaiorAridadeGanha(string nome) => Nome = nome;

        public MaiorAridadeGanha(string nome, decimal preco)
        {
            Nome = nome;
            Preco = preco;
        }

        public string Nome { get; }

        public decimal Preco { get; }
    }

    [Fact]
    public void TheConstructorWithMostParameters_Wins()
    {
        MaiorAridadeGanha dto = Map<MaiorAridadeGanha>(Row(("[Nome]", "Mesa"), ("[Preco]", 10m)));

        Assert.Equal("Mesa", dto.Nome);
        Assert.Equal(10m, dto.Preco);
    }
}
