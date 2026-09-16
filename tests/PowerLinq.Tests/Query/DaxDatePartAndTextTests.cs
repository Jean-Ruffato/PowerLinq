using System.Linq.Expressions;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Syntax;
using PowerLinq.DaxConverter.Translators;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Date parts and text methods over a column. Several of these cases used to be refused, and the
/// corresponding refusal tests lived in <see cref="DaxParameterDependencyTests"/> — turning one
/// into the other is the cycle CONTRIBUTING describes.
/// </summary>
public sealed class DaxDatePartAndTextTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Data]")] public DateTime Data { get; set; }
        [DaxColumn("Produto[DataOpcional]")] public DateTime? DataOpcional { get; set; }
        [DaxColumn("Produto[Quantidade]")] public int? Quantidade { get; set; }
    }

    private static string Translate(Expression body) =>
        new DaxExpressionVisitor("Produto").Translate(body).ToDaxString();

    // ---------- date parts ----------

    [Fact]
    public void Year_BecomesYear()
    {
        Expression<Func<Produto, bool>> predicate = p => p.Data.Year == 2024;

        Assert.Equal("YEAR(Produto[Data]) = 2024", Translate(predicate.Body));
    }

    [Theory]
    [InlineData("Month", "MONTH")]
    [InlineData("Day", "DAY")]
    [InlineData("Hour", "HOUR")]
    [InlineData("Minute", "MINUTE")]
    [InlineData("Second", "SECOND")]
    public void DateParts_MapToTheDirectFunction(string member, string function)
    {
        // Builds the expression by reflection to cover all five without repeating the body.
        ParameterExpression parameter = Expression.Parameter(typeof(Produto), "p");
        MemberExpression column = Expression.Property(parameter, nameof(Produto.Data));
        MemberExpression part = Expression.Property(column, member);
        BinaryExpression body = Expression.Equal(part, Expression.Constant(1));

        Assert.Equal($"{function}(Produto[Data]) = 1", Translate(body));
    }

    [Fact]
    public void DayOfWeek_IsShiftedToMatchDotNet()
    {
        // WEEKDAY with type 1 gives 1 for Sunday; .NET's DayOfWeek gives 0. Without the -1 the
        // value would come out one day off — the kind of error that does not fail, only lies.
        Expression<Func<Produto, bool>> predicate = p => p.Data.DayOfWeek == DayOfWeek.Sunday;

        Assert.Equal("WEEKDAY(Produto[Data], 1) - 1 = 0", Translate(predicate.Body));
    }

    [Fact]
    public void Date_RecomposesFromTheParts()
    {
        // DAX has no function to truncate the time, so the date is recomposed.
        var inicio = new DateTime(2024, 1, 1);
        Expression<Func<Produto, bool>> predicate = p => p.Data.Date == inicio;

        Assert.Equal(
            "DATE(YEAR(Produto[Data]), MONTH(Produto[Data]), DAY(Produto[Data])) = DATE(2024,1,1)",
            Translate(predicate.Body));
    }

    [Fact]
    public void DatePartComposesWithOtherPredicates()
    {
        Expression<Func<Produto, bool>> predicate = p => p.Data.Year == 2024 && p.Data.Month >= 6;

        Assert.Equal(
            "YEAR(Produto[Data]) = 2024 && MONTH(Produto[Data]) >= 6",
            Translate(predicate.Body));
    }

    // ---------- nullable column ----------

    [Fact]
    public void NullableValue_IsIdentity()
    {
        // In DAX a nullable column is the same column. Without this, a date part over a DateTime?
        // would be unreachable, because in C# the only way there is through .Value.
        Expression<Func<Produto, bool>> predicate = p => p.Quantidade!.Value > 0;

        Assert.Equal("Produto[Quantidade] > 0", Translate(predicate.Body));
    }

    [Fact]
    public void DatePartOverANullableDate_Works()
    {
        Expression<Func<Produto, bool>> predicate = p => p.DataOpcional!.Value.Year == 2024;

        Assert.Equal("YEAR(Produto[DataOpcional]) = 2024", Translate(predicate.Body));
    }

    // ---------- text methods ----------

    [Fact]
    public void Length_BecomesLen()
    {
        Expression<Func<Produto, bool>> predicate = p => p.Nome.Length > 3;

        Assert.Equal("LEN(Produto[Nome]) > 3", Translate(predicate.Body));
    }

    [Fact]
    public void Substring_WithStartAndLength_BecomesMidOneBased()
    {
        // MID is 1-based, Substring is 0-based: the literal index 0 becomes 1 during translation.
        Expression<Func<Produto, bool>> predicate = p => p.Nome.Substring(0, 1) == "A";

        Assert.Equal("MID(Produto[Nome], 1, 1) = \"A\"", Translate(predicate.Body));
    }

    [Fact]
    public void Substring_WithANonZeroStart_ShiftsByOne()
    {
        Expression<Func<Produto, bool>> predicate = p => p.Nome.Substring(2, 3) == "ABC";

        Assert.Equal("MID(Produto[Nome], 3, 3) = \"ABC\"", Translate(predicate.Body));
    }

    [Fact]
    public void Substring_WithoutLength_RunsToTheEnd()
    {
        Expression<Func<Produto, bool>> predicate = p => p.Nome.Substring(1) == "BC";

        Assert.Equal(
            "MID(Produto[Nome], 2, LEN(Produto[Nome])) = \"BC\"",
            Translate(predicate.Body));
    }

    [Fact]
    public void Substring_WithAComputedStart_AddsTheOffsetInDax()
    {
        // A non-literal index cannot be added during translation, so the +1 shows up in the DAX.
        Expression<Func<Produto, bool>> predicate = p => p.Nome.Substring(p.Nome.Length - 2, 1) == "Z";

        Assert.Equal(
            "MID(Produto[Nome], LEN(Produto[Nome]) - 2 + 1, 1) = \"Z\"",
            Translate(predicate.Body));
    }

    [Fact]
    public void Replace_BecomesSubstitute()
    {
        Expression<Func<Produto, bool>> predicate = p => p.Nome.Replace("-", "") == "AB";

        Assert.Equal("SUBSTITUTE(Produto[Nome], \"-\", \"\") = \"AB\"", Translate(predicate.Body));
    }

    [Fact]
    public void IndexOf_IsShiftedToZeroBased()
    {
        // SEARCH is 1-based and returns 0 when it finds nothing; IndexOf is 0-based and returns
        // -1. The same -1 aligns both cases.
        Expression<Func<Produto, bool>> predicate = p => p.Nome.IndexOf("A") == 0;

        Assert.Equal("SEARCH(\"A\", Produto[Nome], 1, 0) - 1 = 0", Translate(predicate.Body));
    }

    [Fact]
    public void IndexOf_NotFoundMapsToMinusOne()
    {
        // Documents the equivalence: SEARCH returns 0, minus 1 gives -1, which is IndexOf's contract.
        Expression<Func<Produto, bool>> predicate = p => p.Nome.IndexOf("A") == -1;

        Assert.Equal("SEARCH(\"A\", Produto[Nome], 1, 0) - 1 = -1", Translate(predicate.Body));
    }

    [Fact]
    public void TextMethodsCompose()
    {
        Expression<Func<Produto, bool>> predicate =
            p => p.Nome.Substring(0, 3).ToUpper() == "ABC" && p.Nome.Length > 5;

        Assert.Equal(
            "UPPER(MID(Produto[Nome], 1, 3)) = \"ABC\" && LEN(Produto[Nome]) > 5",
            Translate(predicate.Body));
    }

    // ---------- what still has no translation ----------

    [Theory]
    [InlineData("DayOfYear")]
    [InlineData("Ticks")]
    [InlineData("TimeOfDay")]
    public void DateMembersWithoutADirectEquivalent_AreStillRefused(string member)
    {
        ParameterExpression parameter = Expression.Parameter(typeof(Produto), "p");
        MemberExpression part = Expression.Property(
            Expression.Property(parameter, nameof(Produto.Data)),
            member);

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Translate(Expression.Convert(part, typeof(object))));

        Assert.Contains($"Produto.Data.{member}", ex.Message);
    }

    [Fact]
    public void PadLeft_IsStillRefused()
    {
        // It would require composing REPT with the concatenation operator, which is not emitted yet.
        Expression<Func<Produto, bool>> predicate = p => p.Nome.PadLeft(5) == "A";

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Translate(predicate.Body));

        Assert.Contains("PadLeft", ex.Message);
    }
}
