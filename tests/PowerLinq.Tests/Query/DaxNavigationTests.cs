using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Model;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Navigation between entities: <c>r.Exporter.CnpjRoot</c> instead of a textual qualified reference.
/// </summary>
/// <remarks>
/// <para>
/// The relationship is the most important piece of information in a star model, and before this it
/// existed only as a convention inside a string — <c>[DaxColumn("'DIM_EMPRESA'[CNPJ_RAIZ]")]</c>.
/// </para>
/// <para>
/// <b>The translation depends on the position, and that is the delicate point.</b> In row context
/// another table's column needs <c>RELATED</c>; as a grouping column of a
/// <c>SUMMARIZECOLUMNS</c> — which opens no row context — <c>RELATED</c> would be <b>wrong</b>, and
/// the bare qualified reference is the right form. Same C# expression, two DAX outputs.
/// </para>
/// </remarks>
public sealed class DaxNavigationTests
{
    [DaxTable("DIM_GRUPO")]
    private sealed class Grupo
    {
        [DaxColumn("DIM_GRUPO[ID]")] public long Id { get; set; }
        [DaxColumn("DIM_GRUPO[NOME]")] public string Nome { get; set; } = "";
    }

    [DaxTable("DIM_EMPRESA")]
    private sealed class Empresa
    {
        [DaxColumn("DIM_EMPRESA[ID]")] public long Id { get; set; }
        [DaxColumn("DIM_EMPRESA[CNPJ_RAIZ]")] public string CnpjRaiz { get; set; } = "";
        [DaxColumn("DIM_EMPRESA[LIMITE]")] public decimal Limite { get; set; }

        [DaxColumn("DIM_EMPRESA[GRUPO_ID]")]
        [DaxNavigation("DIM_EMPRESA[GRUPO_ID]")]
        public Grupo Grupo { get; set; } = new();
    }

    [DaxTable("FAT_ISENCAO")]
    private sealed class Fato
    {
        [DaxColumn("FAT_ISENCAO[VALOR]")] public decimal Valor { get; set; }

        [DaxColumn("FAT_ISENCAO[EMPRESA_ID]")]
        [DaxNavigation("FAT_ISENCAO[EMPRESA_ID]")]
        public Empresa Empresa { get; set; } = new();
    }

    private sealed class Projetado
    {
        [DaxColumn("Cnpj")] public string Cnpj { get; set; } = "";
    }

    private sealed class PorEmpresa
    {
        [DaxColumn("DIM_EMPRESA[CNPJ_RAIZ]")] public string Cnpj { get; set; } = "";
        [DaxColumn("[Total]")] public decimal? Total { get; set; }
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

    private static DaxTable<Fato> Table() => new(new NoopExecutor());

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    // ---------- a separable predicate: filter context, not RELATED ----------

    /// <summary>
    /// <b>A navigation in a separable predicate does NOT become <c>RELATED</c>.</b> It becomes a
    /// filter table argument, and that preference is explicit.
    /// </summary>
    /// <remarks>
    /// It is not a matter of preference: <c>RELATED</c> inside <c>FILTER</c> is row-by-row iteration
    /// in the <i>formula engine</i>, and <c>CALCULATETABLE</c>'s boolean pushes down as a filter to
    /// the <i>storage engine</i> — same result, costs of a different order on a large fact.
    /// Emitting <c>RELATED</c> here would be a silent performance trap.
    /// </remarks>
    [Fact]
    public void ASeparablePredicate_GoesToFilterContextAndNotToRelated()
    {
        string dax = Flat(Table().Where(r => r.Empresa.CnpjRaiz == "60857349").ToDaxString());

        Assert.Equal(
            "EVALUATE CALCULATETABLE(FAT_ISENCAO, "
            + "FILTER(DIM_EMPRESA, DIM_EMPRESA[CNPJ_RAIZ] = \"60857349\"))",
            dax);
        Assert.DoesNotContain("RELATED", dax, StringComparison.Ordinal);
    }

    // ---------- what only exists in row context: RELATED ----------

    /// <summary>
    /// <b>It is the case that only exists in row context</b>, and it is the reason navigation
    /// cannot be replaced by a context filter: comparing columns of <b>two different tables</b>. A
    /// filter table argument does not express that.
    /// </summary>
    [Fact]
    public void ComparingColumnsOfTwoTables_NeedsRowContext()
    {
        string dax = Flat(Table().Where(r => r.Valor > r.Empresa.Limite).ToDaxString());

        Assert.Equal(
            "EVALUATE FILTER(FAT_ISENCAO, FAT_ISENCAO[VALOR] > RELATED(DIM_EMPRESA[LIMITE]))", dax);
    }

    /// <summary>
    /// Two hops, and the filter applies to the table at the <b>end of the chain</b> — not to the
    /// middle one. The relationship propagates the filter from there to the fact, which is what a star model does.
    /// </summary>
    [Fact]
    public void ATwoHopSeparablePredicate_FiltersTheFarTable()
    {
        string dax = Flat(Table().Where(r => r.Empresa.Grupo.Nome == "ACME").ToDaxString());

        Assert.Equal(
            "EVALUATE CALCULATETABLE(FAT_ISENCAO, FILTER(DIM_GRUPO, DIM_GRUPO[NOME] = \"ACME\"))",
            dax);
    }

    [Fact]
    public void NavigationInAnOrderBy_BecomesRelated()
    {
        string dax = Flat(Table().OrderBy(r => r.Empresa.CnpjRaiz).ToDaxString());

        Assert.EndsWith("ORDER BY RELATED(DIM_EMPRESA[CNPJ_RAIZ]) ASC", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// In a projection the <c>SELECTCOLUMNS</c> <b>does</b> open a row context, so another table's
    /// column needs <c>RELATED</c> there too.
    /// </summary>
    /// <remarks>
    /// <b>This diverged from the initial design</b>, which proposed "a grouping or projection
    /// column → a direct qualified reference". For grouping that is right; for a projection, a bare
    /// reference inside <c>SELECTCOLUMNS</c> is the <i>"a single value cannot be determined"</i>
    /// error, for the same reason <c>DaxCalculateTable</c> documents about <c>FILTER</c>. It was not
    /// possible to probe this against a real model in this work.
    /// </remarks>
    [Fact]
    public void NavigationInAProjection_BecomesRelatedBecauseSelectColumnsHasRowContext()
    {
        string dax = Flat(Table()
            .Select(r => new Projetado { Cnpj = r.Empresa.CnpjRaiz })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SELECTCOLUMNS(FAT_ISENCAO, \"Cnpj\", RELATED(DIM_EMPRESA[CNPJ_RAIZ]))", dax);
    }

    // ---------- without row context: a qualified reference ----------

    /// <summary>
    /// <b>As a grouping column, with no <c>RELATED</c>.</b> <c>SUMMARIZECOLUMNS</c> opens no row
    /// context: it groups by the model's columns and resolves through the relationships, and
    /// <c>RELATED</c> there is a DAX error — not merely unnecessary.
    /// </summary>
    [Fact]
    public void NavigationAsAGroupingKey_IsTheQualifiedReferenceWithoutRelated()
    {
        string dax = Flat(Table()
            .GroupBy(r => r.Empresa.CnpjRaiz)
            .Select(g => new PorEmpresa { Cnpj = g.Key, Total = g.Sum(r => r.Valor) })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(DIM_EMPRESA[CNPJ_RAIZ], "
            + "\"Total\", SUM(FAT_ISENCAO[VALOR]))",
            dax);
        Assert.DoesNotContain("RELATED", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// All three rules in a single query: the grouping key as a bare qualified reference, the
    /// separable predicate as a filter table, and no <c>RELATED</c> where it is not needed.
    /// </summary>
    [Fact]
    public void BothFormsCoexist_EachInItsOwnPosition()
    {
        string dax = Flat(Table()
            .Where(r => r.Empresa.Grupo.Nome == "ACME")
            .GroupBy(r => r.Empresa.CnpjRaiz)
            .Select(g => new PorEmpresa { Cnpj = g.Key, Total = g.Sum(r => r.Valor) })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(DIM_EMPRESA[CNPJ_RAIZ], "
            + "FILTER(DIM_GRUPO, DIM_GRUPO[NOME] = \"ACME\"), "
            + "\"Total\", SUM(FAT_ISENCAO[VALOR]))",
            dax);
        Assert.DoesNotContain("RELATED", dax, StringComparison.Ordinal);
    }

    // ---------- what navigation is not ----------

    /// <summary>
    /// <b>A navigation is not a column, and is not materialized.</b> <c>EVALUATE Fact</c> returns
    /// the fact's columns, not the dimension's entity — mapping the property would make
    /// materialization fail on an unsupported type, pointing at a problem that is not what happened.
    /// </summary>
    [Fact]
    public void ANavigationProperty_IsNotAColumnOfTheEntity()
    {
        IEnumerable<string> chaves =
            PowerLinq.DaxConverter.Mapping.EntityMapper.GetColumnMappings(typeof(Fato)).Keys;

        Assert.DoesNotContain("FAT_ISENCAO[Empresa]", chaves, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("FAT_ISENCAO[VALOR]", chaves, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A nested member that is <b>not</b> a navigation keeps its own translation:
    /// <c>p.Data.Year</c> and <c>p.Nome.Length</c> would come through here, and the recognizer has to say "not my case".
    /// </summary>
    [Fact]
    public void ANestedMemberThatIsNotANavigation_KeepsItsOwnTranslation()
    {
        string dax = Flat(Table()
            .Where(r => r.Empresa.CnpjRaiz.Length > 8)
            .ToDaxString());

        Assert.Equal(
            "EVALUATE CALCULATETABLE(FAT_ISENCAO, "
            + "FILTER(DIM_EMPRESA, LEN(DIM_EMPRESA[CNPJ_RAIZ]) > 8))",
            dax);
    }

    // ---------- direction and ambiguity, against the schema ----------

    private static DaxModelSchema Schema(
        DaxCardinality fatoLado = DaxCardinality.Many,
        bool ativo = true,
        bool ambiguo = false)
    {
        DaxSchemaRelationship[] relacionamentos = ambiguo
            ?
            [
                new("FAT_ISENCAO", "EMPRESA_ID", DaxCardinality.Many, "DIM_EMPRESA", "ID", DaxCardinality.One, true),
                new("FAT_ISENCAO", "VALOR", DaxCardinality.Many, "DIM_EMPRESA", "LIMITE", DaxCardinality.One, true)
            ]
            :
            [
                new("FAT_ISENCAO", "EMPRESA_ID", fatoLado, "DIM_EMPRESA", "ID",
                    fatoLado == DaxCardinality.Many ? DaxCardinality.One : DaxCardinality.Many, ativo)
            ];

        return new DaxModelSchema(
            [
                new DaxSchemaTable("FAT_ISENCAO",
                    [
                        new DaxSchemaColumn("VALOR", "Decimal", false),
                        new DaxSchemaColumn("EMPRESA_ID", "Int64", false)
                    ], false),
                new DaxSchemaTable("DIM_EMPRESA",
                    [
                        new DaxSchemaColumn("ID", "Int64", false),
                        new DaxSchemaColumn("CNPJ_RAIZ", "String", false),
                        new DaxSchemaColumn("LIMITE", "Decimal", false),
                        new DaxSchemaColumn("GRUPO_ID", "Int64", false)
                    ], false),
                new DaxSchemaTable("DIM_GRUPO",
                    [
                        new DaxSchemaColumn("ID", "Int64", false),
                        new DaxSchemaColumn("NOME", "String", false)
                    ], false)
            ],
            [],
            [
                .. relacionamentos,
                new("DIM_EMPRESA", "GRUPO_ID", DaxCardinality.Many, "DIM_GRUPO", "ID", DaxCardinality.One, true)
            ]);
    }

    /// <summary>A well-formed navigation produces no divergence.</summary>
    [Fact]
    public void AWellFormedNavigation_HasNoDivergence()
    {
        Assert.Empty(DaxContractValidator.Validate(Schema(), [typeof(Fato)]));
    }

    /// <summary>
    /// <b>A wrong direction fails while quoting the tables and the direction.</b> The check lives
    /// here, and not in the translation, because at translation time there is no way to know the
    /// cardinality — composing a query touches no network.
    /// </summary>
    [Fact]
    public void ANavigationThatWouldNeedOneToMany_IsReportedWithTablesAndDirection()
    {
        DaxDivergence divergencia = Assert.Single(
            DaxContractValidator.Validate(Schema(fatoLado: DaxCardinality.One), [typeof(Fato)]));

        Assert.Equal(DaxDivergenceKind.UntraversableDirection, divergencia.Kind);
        Assert.Equal("Fato.Empresa", divergencia.Where);
        Assert.Contains("one-to-many", divergencia.Message, StringComparison.Ordinal);
        Assert.Contains("FAT_ISENCAO", divergencia.Message, StringComparison.Ordinal);
        Assert.Contains("DIM_EMPRESA", divergencia.Message, StringComparison.Ordinal);
    }

    /// <summary>An ambiguous path fails while naming both, without picking one.</summary>
    [Fact]
    public void AnAmbiguousRelationship_IsReportedNamingBothPaths()
    {
        DaxDivergence divergencia = Assert.Single(
            DaxContractValidator.Validate(Schema(ambiguo: true), [typeof(Fato)]));

        Assert.Equal(DaxDivergenceKind.AmbiguousRelationship, divergencia.Kind);
        Assert.Contains("EMPRESA_ID", divergencia.Message, StringComparison.Ordinal);
        Assert.Contains("VALOR", divergencia.Message, StringComparison.Ordinal);
    }

    /// <summary>An inactive relationship is told apart from a missing one.</summary>
    [Fact]
    public void AnInactiveRelationship_IsReportedAsInactive()
    {
        DaxDivergence divergencia = Assert.Single(
            DaxContractValidator.Validate(Schema(ativo: false), [typeof(Fato)]));

        Assert.Equal(DaxDivergenceKind.InactiveRelationship, divergencia.Kind);
        Assert.Contains("USERELATIONSHIP", divergencia.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Each hop is checked, and the message names what broke.</b> <c>RELATED</c> crosses the
    /// whole chain, but only if every link is many-to-one — saying merely "the navigation failed"
    /// would send people looking in the wrong place.
    /// </summary>
    [Fact]
    public void EachHopIsChecked_AndTheBrokenOneIsNamed()
    {
        var schema = new DaxModelSchema(
            Schema().Tables,
            [],
            [
                new("FAT_ISENCAO", "EMPRESA_ID", DaxCardinality.Many, "DIM_EMPRESA", "ID", DaxCardinality.One, true),
                // The second hop reversed: the company sits on the ONE side relative to the group.
                new("DIM_EMPRESA", "GRUPO_ID", DaxCardinality.One, "DIM_GRUPO", "ID", DaxCardinality.Many, true)
            ]);

        DaxDivergence divergencia = Assert.Single(
            DaxContractValidator.Validate(schema, [typeof(Fato)]));

        Assert.Equal(DaxDivergenceKind.UntraversableDirection, divergencia.Kind);
        Assert.Equal("Empresa.Grupo", divergencia.Where);
        Assert.Contains("DIM_GRUPO", divergencia.Message, StringComparison.Ordinal);
    }
}
