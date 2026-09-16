using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Model;

namespace PowerLinq.Tests.Model;

/// <summary>
/// Contract generation from the schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these tests really verify is that the generated code compiles.</b> For a generator that
/// is the property that matters: a test that only checked the text would pass with invalid C#, and
/// the error would show up in the consumer's build. That is why the suite compiles the output with
/// Roslyn, and it is the only reason <c>Microsoft.CodeAnalysis.CSharp</c> is in the test project.
/// </para>
/// <para>
/// The schema used is chosen to hurt: names with spaces, with accents, with symbols, starting with
/// a digit, and two names that collapse into the same identifier.
/// </para>
/// </remarks>
public sealed class DaxContractGeneratorTests
{
    /// <summary>
    /// A schema with the names a real model has, and which a naive generator would break on.
    /// </summary>
    private static DaxModelSchema Hostile() => new(
        [
            new DaxSchemaTable(
                "Ordem de Venda",
                [
                    new DaxSchemaColumn("VL_REALIZADO_BRL", "Decimal", IsHidden: false),
                    new DaxSchemaColumn("Início da Operação", "DateTime", IsHidden: false),
                    new DaxSchemaColumn("2024", "Int64", IsHidden: false),
                    new DaxSchemaColumn("Valor R$", "Decimal", IsHidden: false),
                    new DaxSchemaColumn("Valor (R$)", "Decimal", IsHidden: false),
                    new DaxSchemaColumn("Ativo?", "Boolean", IsHidden: false),
                    new DaxSchemaColumn("Anexo", "Binary", IsHidden: false)
                ],
                IsHidden: false),
            new DaxSchemaTable(
                "DIM_EMPRESA",
                [new DaxSchemaColumn("CNPJ_RAIZ", "String", IsHidden: false)],
                IsHidden: false)
        ],
        [
            new DaxSchemaMeasure("Total Vendas", "Ordem de Venda", "Decimal", IsHidden: false),
            new DaxSchemaMeasure("Margem %", "Ordem de Venda", "Double", IsHidden: false)
        ],
        []);

    /// <summary>
    /// Compiles the generated code and returns the errors — empty when it compiles.
    /// </summary>
    /// <remarks>
    /// <c>Error</c> only. A warning from an <c>auto-generated</c> file is not the subject: the
    /// generated code carries the header that excludes it from style analysis, and what is asserted here is that it is valid C#.
    /// </remarks>
    private static ImmutableArray<Diagnostic> Compile(string source)
    {
        var compilation = CSharpCompilation.Create(
            "Gerado",
            [CSharpSyntaxTree.ParseText(source)],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        return [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    /// <remarks>
    /// The references come from the current domain: the generated code needs <c>[DaxColumn]</c>, so
    /// the converter's assembly has to be among them — and listing paths by hand would break on
    /// every TFM change.
    /// </remarks>
    private static IEnumerable<MetadataReference> References() =>
        AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));

    // ---------- the property that matters ----------

    [Fact]
    public void TheGeneratedCode_Compiles()
    {
        string source = DaxContractGenerator.Generate(Hostile(), "Contratos.Gerados");

        ImmutableArray<Diagnostic> errors = Compile(source);

        Assert.True(
            errors.IsEmpty,
            "O código gerado não compila:\n"
            + string.Join("\n", errors.Select(e => e.ToString()))
            + "\n\n--- gerado ---\n"
            + source);
    }

    /// <summary>
    /// And it compiles <b>with</b> the attributes actually applied: the previous test would prove
    /// valid syntax even if the generator emitted an attribute that does not exist.
    /// </summary>
    [Fact]
    public void TheGeneratedContracts_CarryTheMappingAttributes()
    {
        string source = DaxContractGenerator.Generate(Hostile(), "Contratos.Gerados");

        Assert.Contains("[DaxTable(\"Ordem de Venda\")]", source, StringComparison.Ordinal);

        // A table with a space in its name comes out in single quotes in the column REFERENCE,
        // which is how DAX qualifies a name with a space — without it the server refuses.
        Assert.Contains(
            "[DaxColumn(\"'Ordem de Venda'[VL_REALIZADO_BRL]\")]", source, StringComparison.Ordinal);

        // And a table without a space comes out unquoted, because adding quotes would change the DAX generated today.
        Assert.Contains("[DaxColumn(\"DIM_EMPRESA[CNPJ_RAIZ]\")]", source, StringComparison.Ordinal);
    }

    // ---------- the names ----------

    /// <summary>
    /// <c>VL_REALIZADO_BRL</c> becomes <c>VlRealizadoBrl</c>, and the accent is <b>preserved</b> —
    /// C# accepts it, and transliterating would change the name whoever reads the model recognizes.
    /// </summary>
    [Fact]
    public void ModelNames_BecomeIdiomaticIdentifiersKeepingAccents()
    {
        string source = DaxContractGenerator.Generate(Hostile(), "Contratos.Gerados");

        Assert.Contains("VlRealizadoBrl", source, StringComparison.Ordinal);
        Assert.Contains("InícioDaOperação", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A column whose name starts with a digit — <c>2024</c>, common in a calendar model — gets a
    /// prefix, because a C# identifier cannot start with a digit.
    /// </summary>
    [Fact]
    public void AColumnStartingWithADigit_GetsAValidIdentifier()
    {
        string source = DaxContractGenerator.Generate(Hostile(), "Contratos.Gerados");

        Assert.Contains("_2024", source, StringComparison.Ordinal);
        Assert.True(Compile(source).IsEmpty);
    }

    /// <summary>
    /// <c>Valor R$</c> and <c>Valor (R$)</c> collapse into the same identifier. <b>Both</b> stay,
    /// with a suffix: silently dropping one would lose a column of the model — and the symptom
    /// would be a contract with no way to read a value that exists.
    /// </summary>
    [Fact]
    public void TwoColumnsThatCollapseToTheSameIdentifier_BothSurvive()
    {
        string source = DaxContractGenerator.Generate(Hostile(), "Contratos.Gerados");

        Assert.Contains("[DaxColumn(\"'Ordem de Venda'[Valor R$]\")]", source, StringComparison.Ordinal);
        Assert.Contains("[DaxColumn(\"'Ordem de Venda'[Valor (R$)]\")]", source, StringComparison.Ordinal);
        // The symbol drops out of the identifier, so both names collapse into ValorR — and the
        // second gets a suffix.
        Assert.Contains("ValorR {", source, StringComparison.Ordinal);
        Assert.Contains("ValorR2 {", source, StringComparison.Ordinal);
        Assert.True(Compile(source).IsEmpty);
    }

    // ---------- the measures ----------

    /// <summary>
    /// <b>It is what closes the measure criterion.</b> Reflection cannot reach a measure name — it
    /// is an argument of <c>MeasureAsync</c> written at the call site. With a generated constant, a
    /// typo becomes a <b>compile</b> error, which is where it should die.
    /// </summary>
    [Fact]
    public void MeasuresBecomeConstants_SoATypoIsACompileError()
    {
        string source = DaxContractGenerator.Generate(Hostile(), "Contratos.Gerados");

        Assert.Contains("public const string TotalVendas = \"Total Vendas\";", source, StringComparison.Ordinal);
        Assert.Contains("public const string Margem = \"Margem %\";", source, StringComparison.Ordinal);
    }

    /// <summary>With no measure in the model, the constants class is not emitted empty.</summary>
    [Fact]
    public void AModelWithoutMeasures_GetsNoConstantsClass()
    {
        var schema = new DaxModelSchema(
            [new DaxSchemaTable("T", [new DaxSchemaColumn("C", "String", IsHidden: false)], IsHidden: false)],
            [],
            []);

        Assert.DoesNotContain("class Measures", DaxContractGenerator.Generate(schema, "X"), StringComparison.Ordinal);
    }

    // ---------- the types ----------

    /// <summary>
    /// Everything nullable except <c>string</c>: in DAX every column can return <c>BLANK</c>, and a
    /// non-nullable <c>decimal</c> would erase the difference between "summed to zero" and "there
    /// was no row" — the same decision <c>MinAsync</c> and <c>MeasureAsync</c> already make.
    /// </summary>
    [Fact]
    public void ColumnTypesAreNullable_BecauseEveryDaxColumnCanBeBlank()
    {
        string source = DaxContractGenerator.Generate(Hostile(), "Contratos.Gerados");

        Assert.Contains("public decimal? VlRealizadoBrl { get; set; }", source, StringComparison.Ordinal);
        Assert.Contains("public DateTime? InícioDaOperação { get; set; }", source, StringComparison.Ordinal);
        Assert.Contains("public bool? Ativo { get; set; }", source, StringComparison.Ordinal);
        Assert.Contains("public long? _2024 { get; set; }", source, StringComparison.Ordinal);

        // string gets an initializer instead of a `?`, so the contract is not born with a null warning.
        Assert.Contains("public string Cnpj", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A type materialization does not convert — <c>Binary</c> — comes out as <c>string</c>, which
    /// is what the server delivers and what the type table accepts.
    /// </summary>
    [Fact]
    public void ATypeMaterializationCannotConvert_FallsBackToString()
    {
        string source = DaxContractGenerator.Generate(Hostile(), "Contratos.Gerados");

        Assert.Contains("public string Anexo", source, StringComparison.Ordinal);
    }

    // ---------- round trip ----------

    /// <summary>
    /// The full cycle: the generated code is validated <b>against the same schema</b> and reports
    /// nothing. If generation and validation disagreed about the shape of a column reference — the
    /// single quotes of a name with a space, for example — this test would fail.
    /// </summary>
    [Fact]
    public void WhatTheGeneratorEmits_TheValidatorAccepts()
    {
        DaxModelSchema schema = Hostile();
        string source = DaxContractGenerator.Generate(schema, "Contratos.Gerados");

        var compilation = CSharpCompilation.Create(
            "GeradoParaValidar",
            [CSharpSyntaxTree.ParseText(source)],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        EmitResult emitted = compilation.Emit(stream);

        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(
            d => d.Severity == DiagnosticSeverity.Error)));

        stream.Position = 0;
        Assembly assembly = Assembly.Load(stream.ToArray());

        IReadOnlyList<DaxDivergence> divergencias =
            DaxContractValidator.Validate(schema, assembly);

        Assert.Empty(divergencias);

        // And the generated constants name measures the schema does have.
        Type measures = Assert.Single(assembly.GetTypes(), t => t.Name == "Measures");

        foreach (FieldInfo constant in measures.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            Assert.Null(DaxContractValidator.ValidateMeasure(
                schema, (string)constant.GetRawConstantValue()!, constant.Name));
        }
    }
}
