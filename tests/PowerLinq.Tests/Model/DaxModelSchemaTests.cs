using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Model;

namespace PowerLinq.Tests.Model;

/// <summary>
/// The model's schema, its file, contract validation and generation.
/// </summary>
/// <remarks>
/// <para>
/// Three open items called for the same capability — reading the model's metadata — and the answer
/// was contract generation, with validation as the safety net.
/// </para>
/// <para>
/// <b>Everything here runs offline.</b> That is the property to preserve: the only piece that
/// touches the network is <c>DaxSchemaReader</c>, called explicitly, and what it produces
/// serializes to a file. From the file onwards, generation and validation have no endpoint.
/// </para>
/// </remarks>
public sealed class DaxModelSchemaTests
{
    private static DaxModelSchema Schema() => new(
        [
            new DaxSchemaTable(
                "FAT_ISENCAO",
                [
                    new DaxSchemaColumn("PERIODO_FECHAMENTO", "String", IsHidden: false),
                    new DaxSchemaColumn("VL_REALIZADO_BRL", "Decimal", IsHidden: false),
                    new DaxSchemaColumn("EMPRESA_ID", "Int64", IsHidden: false)
                ],
                IsHidden: false),
            new DaxSchemaTable(
                "DIM_EMPRESA",
                [
                    new DaxSchemaColumn("ID", "Int64", IsHidden: false),
                    new DaxSchemaColumn("CNPJ_RAIZ", "String", IsHidden: false)
                ],
                IsHidden: false)
        ],
        [
            new DaxSchemaMeasure("Total Vendas", "FAT_ISENCAO", "Decimal", IsHidden: false)
        ],
        [
            new DaxSchemaRelationship(
                "FAT_ISENCAO", "EMPRESA_ID", DaxCardinality.Many,
                "DIM_EMPRESA", "ID", DaxCardinality.One,
                IsActive: true)
        ]);

    // ---------- lookup ----------

    /// <summary>
    /// The lookup is case-insensitive because DAX is: a case-sensitive index would report a column
    /// the server accepts as missing — the worst way to be wrong in a tool whose reason for
    /// existing is to point out divergence.
    /// </summary>
    [Fact]
    public void LookupIgnoresCase_BecauseDaxDoes()
    {
        DaxModelSchema schema = Schema();

        Assert.NotNull(schema.Table("fat_isencao"));
        Assert.True(schema.HasColumn("FAT_ISENCAO", "periodo_fechamento"));
        Assert.True(schema.HasMeasure("total vendas"));
    }

    /// <summary>
    /// A measure name is accepted with and without brackets: the model stores <c>Total Vendas</c>
    /// and whoever writes DAX writes <c>[Total Vendas]</c>. Demanding one of the forms would turn
    /// notation into a false positive divergence.
    /// </summary>
    [Fact]
    public void AMeasureNameIsAccepted_WithAndWithoutBrackets()
    {
        DaxModelSchema schema = Schema();

        Assert.True(schema.HasMeasure("Total Vendas"));
        Assert.True(schema.HasMeasure("[Total Vendas]"));
    }

    // ---------- the file ----------

    /// <summary>Round trip: what the file keeps is what the schema had.</summary>
    [Fact]
    public void TheSchemaFile_RoundTrips()
    {
        DaxModelSchema original = Schema();

        DaxModelSchema lido = DaxSchemaFile.Read(DaxSchemaFile.Write(original));

        Assert.Equal(original.Tables.Count, lido.Tables.Count);
        Assert.Equal(original.Measures, lido.Measures);
        Assert.Equal(original.Relationships, lido.Relationships);
        Assert.True(lido.HasColumn("FAT_ISENCAO", "VL_REALIZADO_BRL"));
    }

    /// <summary>
    /// A file of an unknown format is <b>refused</b>, not read for whatever comes out. Reading it
    /// would produce a partial schema, and a partial schema turns into invented divergence: the
    /// tool would report a column the model does have as missing.
    /// </summary>
    [Fact]
    public void AFileFromAFutureFormat_IsRefused()
    {
        string json = DaxSchemaFile.Write(Schema())
            .Replace(
                $"\"FormatVersion\": {DaxSchemaFile.FormatVersion}",
                "\"FormatVersion\": 99",
                StringComparison.Ordinal);

        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(() => DaxSchemaFile.Read(json));

        Assert.Contains("format version 99", ex.Message, StringComparison.Ordinal);
        Assert.Contains("partial schema", ex.Message, StringComparison.Ordinal);
    }

    // ---------- contract validation ----------

    [DaxTable("FAT_ISENCAO")]
    private sealed class ContratoCerto
    {
        [DaxColumn("FAT_ISENCAO[PERIODO_FECHAMENTO]")] public string Periodo { get; set; } = "";
        [DaxColumn("'DIM_EMPRESA'[CNPJ_RAIZ]")] public string Cnpj { get; set; } = "";
    }

    [DaxTable("FAT_ISENCAO")]
    private sealed class ContratoComColunaErrada
    {
        [DaxColumn("FAT_ISENCAO[PERIODO_FECHAMENT]")] public string Periodo { get; set; } = "";
    }

    [DaxTable("FAT_ISENCAOO")]
    private sealed class ContratoComTabelaErrada
    {
        [DaxColumn("FAT_ISENCAOO[PERIODO_FECHAMENTO]")] public string Periodo { get; set; } = "";
    }

    [DaxTable("FAT_ISENCAO")]
    private sealed class ContratoAgregado
    {
        [DaxColumn("FAT_ISENCAO[PERIODO_FECHAMENTO]")] public string Periodo { get; set; } = "";

        // A query OUTPUT column — an extension of a SUMMARIZECOLUMNS. It does not exist in the
        // model and should not.
        [DaxColumn("[Realizado]")] public decimal? Realizado { get; set; }
    }

    [Fact]
    public void AContractThatMatchesTheModel_HasNoDivergence()
    {
        Assert.Empty(DaxContractValidator.Validate(Schema(), [typeof(ContratoCerto)]));
    }

    /// <summary>
    /// A query output column — <c>[Realizado]</c>, with no table — is not checked. It is an
    /// extension of a <c>SUMMARIZECOLUMNS</c>, so reporting it would produce a divergence in every
    /// aggregated-result contract in the project.
    /// </summary>
    [Fact]
    public void AQueryOutputColumn_IsNotCheckedAgainstTheModel()
    {
        Assert.Empty(DaxContractValidator.Validate(Schema(), [typeof(ContratoAgregado)]));
    }

    /// <summary>
    /// The message names both sides <b>and</b> guesses. A typo is the case the feature names as its
    /// motivator, and for it the whole list of names is worse than one guess: a model with three
    /// hundred columns would produce a message nobody reads.
    /// </summary>
    [Fact]
    public void AMistypedColumn_IsReportedWithASuggestion()
    {
        DaxDivergence divergencia = Assert.Single(
            DaxContractValidator.Validate(Schema(), [typeof(ContratoComColunaErrada)]));

        Assert.Equal(DaxDivergenceKind.ColumnNotInModel, divergencia.Kind);
        Assert.Contains("ContratoComColunaErrada.Periodo", divergencia.Where, StringComparison.Ordinal);
        Assert.Contains("PERIODO_FECHAMENT", divergencia.Message, StringComparison.Ordinal);
        Assert.Contains("Did you mean 'PERIODO_FECHAMENTO'?", divergencia.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A wrong table reports <b>one</b> divergence, not one per column: without the table, each
    /// column would repeat the same cause and one contract's report would be twenty lines saying the same thing.
    /// </summary>
    [Fact]
    public void AMistypedTable_IsReportedOnceAndNotPerColumn()
    {
        DaxDivergence divergencia = Assert.Single(
            DaxContractValidator.Validate(Schema(), [typeof(ContratoComTabelaErrada)]));

        Assert.Equal(DaxDivergenceKind.TableNotInModel, divergencia.Kind);
        Assert.Contains("Did you mean 'FAT_ISENCAO'?", divergencia.Message, StringComparison.Ordinal);
    }

    // ---------- measure ----------

    [Fact]
    public void AMeasureThatExists_HasNoDivergence()
    {
        Assert.Null(DaxContractValidator.ValidateMeasure(Schema(), "[Total Vendas]", "Servico.Kpi"));
    }

    [Fact]
    public void AMistypedMeasure_IsReportedWithASuggestion()
    {
        DaxDivergence? divergencia =
            DaxContractValidator.ValidateMeasure(Schema(), "[Total Venda]", "Servico.Kpi");

        Assert.NotNull(divergencia);
        Assert.Equal(DaxDivergenceKind.MeasureNotInModel, divergencia.Kind);
        Assert.Equal("Servico.Kpi", divergencia.Where);
        Assert.Contains("Did you mean 'Total Vendas'?", divergencia.Message, StringComparison.Ordinal);
    }

    // ---------- relationship and cardinality ----------

    /// <summary>
    /// Fact → dimension is <b>many-to-one</b>, which is what <c>RELATED</c> crosses.
    /// </summary>
    [Fact]
    public void NavigatingFromTheManySide_IsAllowed()
    {
        Assert.Null(DaxContractValidator.ValidateNavigation(
            Schema(), "FAT_ISENCAO", "DIM_EMPRESA", "RealizedRow.Exporter"));
    }

    /// <summary>
    /// And the opposite direction is refused while naming the tables and the direction — the case
    /// navigation raised with the RLS table, where the server's error says "the column does not
    /// exist or has no relationship" and does not distinguish a missing column from a wrong direction.
    /// </summary>
    [Fact]
    public void NavigatingFromTheOneSide_IsRefusedNamingTheDirection()
    {
        DaxDivergence? divergencia = DaxContractValidator.ValidateNavigation(
            Schema(), "DIM_EMPRESA", "FAT_ISENCAO", "Empresa.Fatos");

        Assert.NotNull(divergencia);
        Assert.Equal(DaxDivergenceKind.UntraversableDirection, divergencia.Kind);
        Assert.Contains("one-to-many", divergencia.Message, StringComparison.Ordinal);
        Assert.Contains("FAT_ISENCAO[EMPRESA_ID] *—1 DIM_EMPRESA[ID]", divergencia.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two active paths between the same tables is <b>ambiguous</b>, and is refused while naming
    /// both — picking one silently is precisely the problem.
    /// </summary>
    [Fact]
    public void TwoActivePaths_AreReportedAsAmbiguousNamingBoth()
    {
        var schema = new DaxModelSchema(
            Schema().Tables,
            [],
            [
                new DaxSchemaRelationship(
                    "FAT_ISENCAO", "EMPRESA_ID", DaxCardinality.Many,
                    "DIM_EMPRESA", "ID", DaxCardinality.One, IsActive: true),
                new DaxSchemaRelationship(
                    "FAT_ISENCAO", "PERIODO_FECHAMENTO", DaxCardinality.Many,
                    "DIM_EMPRESA", "CNPJ_RAIZ", DaxCardinality.One, IsActive: true)
            ]);

        DaxDivergence? divergencia = DaxContractValidator.ValidateNavigation(
            schema, "FAT_ISENCAO", "DIM_EMPRESA", "RealizedRow.Exporter");

        Assert.NotNull(divergencia);
        Assert.Equal(DaxDivergenceKind.AmbiguousRelationship, divergencia.Kind);
        Assert.Contains("EMPRESA_ID", divergencia.Message, StringComparison.Ordinal);
        Assert.Contains("PERIODO_FECHAMENTO", divergencia.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A single but <b>inactive</b> path is not "there is no relationship": it exists and is
    /// switched off, and the way out for the author is a different one — <c>USERELATIONSHIP</c>, not creating a relationship.
    /// </summary>
    [Fact]
    public void TheOnlyPathBeingInactive_IsNotReportedAsNoRelationship()
    {
        var schema = new DaxModelSchema(
            Schema().Tables,
            [],
            [
                new DaxSchemaRelationship(
                    "FAT_ISENCAO", "EMPRESA_ID", DaxCardinality.Many,
                    "DIM_EMPRESA", "ID", DaxCardinality.One, IsActive: false)
            ]);

        DaxDivergence? divergencia = DaxContractValidator.ValidateNavigation(
            schema, "FAT_ISENCAO", "DIM_EMPRESA", "RealizedRow.Exporter");

        Assert.NotNull(divergencia);
        Assert.Equal(DaxDivergenceKind.InactiveRelationship, divergencia.Kind);
        Assert.Contains("USERELATIONSHIP", divergencia.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPathAtAll_IsReportedAsSuch()
    {
        var schema = new DaxModelSchema(Schema().Tables, [], []);

        DaxDivergence? divergencia = DaxContractValidator.ValidateNavigation(
            schema, "FAT_ISENCAO", "DIM_EMPRESA", "RealizedRow.Exporter");

        Assert.NotNull(divergencia);
        Assert.Equal(DaxDivergenceKind.NoRelationship, divergencia.Kind);
    }

    // ---------- what contract validation lets through ----------

    private sealed class SemAtributoDeTabela
    {
        [DaxColumn("FAT_ISENCAO[NAO_EXISTE]")] public string Coluna { get; set; } = "";
    }

    /// <summary>
    /// A type with no <c>[DaxTable]</c> is not a contract, and is not checked. Without this way
    /// out, any class in the assembly with an annotated property would become a divergence.
    /// </summary>
    [Fact]
    public void ATypeWithoutTheTableAttribute_IsNotAContractAndIsSkipped()
    {
        Assert.Empty(DaxContractValidator.Validate(Schema(), [typeof(SemAtributoDeTabela)]));
    }

    [DaxTable("FAT_ISENCAO")]
    private sealed class ContratoComPropriedadeSemAtributo
    {
        [DaxColumn("FAT_ISENCAO[PERIODO_FECHAMENTO]")] public string Periodo { get; set; } = "";

        /// <summary>No <c>[DaxColumn]</c>: there is no reference to check.</summary>
        public string Calculado { get; set; } = "";
    }

    [Fact]
    public void APropertyWithoutAColumnAttribute_IsSkipped()
    {
        Assert.Empty(DaxContractValidator.Validate(Schema(), [typeof(ContratoComPropriedadeSemAtributo)]));
    }

    [DaxTable("FAT_ISENCAO")]
    private sealed class ContratoComNavegacao
    {
        [DaxColumn("FAT_ISENCAO[EMPRESA_ID]")]
        [DaxNavigation("FAT_ISENCAO[EMPRESA_ID]")]
        public ContratoEmpresa Empresa { get; set; } = new();
    }

    [DaxTable("DIM_EMPRESA")]
    private sealed class ContratoEmpresa
    {
        [DaxColumn("DIM_EMPRESA[CNPJ_RAIZ]")] public string Cnpj { get; set; } = "";
    }

    /// <summary>
    /// A navigation is checked <b>as a relationship</b>, and the foreign key it also carries is not
    /// checked again as a column — it would be the same property counted twice.
    /// </summary>
    [Fact]
    public void ANavigationProperty_IsCheckedAsARelationshipNotAsAColumn()
    {
        Assert.Empty(DaxContractValidator.Validate(Schema(), [typeof(ContratoComNavegacao)]));
    }

    // ---------- a reference to another table ----------

    [DaxTable("FAT_ISENCAO")]
    private sealed class ContratoQueQualificaTabelaInexistente
    {
        [DaxColumn("DIM_EMPRESAA[CNPJ_RAIZ]")] public string Cnpj { get; set; } = "";
    }

    /// <summary>
    /// The reference may qualify a table <b>other</b> than the contract's — it is how
    /// <see cref="ContratoCerto"/> reaches <c>DIM_EMPRESA</c>. When that other one does not exist,
    /// the divergence points at the property, not at the whole contract.
    /// </summary>
    [Fact]
    public void AReferenceToANonexistentForeignTable_IsReportedOnTheProperty()
    {
        DaxDivergence divergencia = Assert.Single(
            DaxContractValidator.Validate(Schema(), [typeof(ContratoQueQualificaTabelaInexistente)]));

        Assert.Equal(DaxDivergenceKind.TableNotInModel, divergencia.Kind);
        Assert.Equal("ContratoQueQualificaTabelaInexistente.Cnpj", divergencia.Where);
        Assert.Contains("DIM_EMPRESAA", divergencia.Message, StringComparison.Ordinal);
        Assert.Contains("Did you mean 'DIM_EMPRESA'?", divergencia.Message, StringComparison.Ordinal);
    }

    // ---------- malformed references ----------

    [DaxTable("FAT_ISENCAO")]
    private sealed class ContratoComReferenciaMalformada
    {
        /// <summary>No brackets at all.</summary>
        [DaxColumn("FAT_ISENCAO.PERIODO")] public string Ponto { get; set; } = "";

        /// <summary>Opens and does not close.</summary>
        [DaxColumn("FAT_ISENCAO[PERIODO")] public string SemFechar { get; set; } = "";

        /// <summary>Empty brackets.</summary>
        [DaxColumn("FAT_ISENCAO[]")] public string Vazio { get; set; } = "";
    }

    /// <summary>
    /// A reference that does not have the <c>Table[Column]</c> shape is <b>ignored</b>, not
    /// reported. Validation checks what it can interpret; guessing the intent of malformed text
    /// would produce invented divergence, which is the worst outcome for a tool whose reason for
    /// existing is to point out real divergence.
    /// </summary>
    [Fact]
    public void AMalformedReference_IsSkippedRatherThanGuessed()
    {
        Assert.Empty(DaxContractValidator.Validate(Schema(), [typeof(ContratoComReferenciaMalformada)]));
    }

    // ---------- the readable form of a divergence ----------

    /// <summary>
    /// <c>ToString</c> is what shows up in the console of whoever runs validation in a build, so it
    /// carries all three pieces: what it is, where, and what.
    /// </summary>
    [Fact]
    public void TheDivergenceRendersKindWhereAndMessage()
    {
        var divergencia = new DaxDivergence(
            DaxDivergenceKind.ColumnNotInModel, "Contrato.Coluna", "não existe");

        Assert.Equal("ColumnNotInModel at Contrato.Coluna: não existe", divergencia.ToString());
    }
}
