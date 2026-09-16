using PowerLinq.DaxConverter.Localization;

namespace PowerLinq.Tests.Query;

public sealed class PowerLinqLocalizationTests
{
    [Fact]
    public void PortugueseInstance_ReadsPortugueseTranslation()
    {
        var localizer = new ResourceManagerPowerLinqLocalizer("pt-BR");

        Assert.Equal("pt-BR", localizer.Culture.Name);
        Assert.Equal("A sequência não contém elementos.", localizer.Get("SequenceEmpty"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr-FR")]
    [InlineData("invalid")]
    public void UnsupportedOrMissingLanguage_FallsBackToEnglish(string? language)
    {
        var localizer = new ResourceManagerPowerLinqLocalizer(language);

        Assert.Equal("en", localizer.Culture.Name);
        Assert.Equal("Sequence contains no elements.", localizer.Get("SequenceEmpty"));
    }

    [Fact]
    public void Instances_AreIndependent()
    {
        var english = new ResourceManagerPowerLinqLocalizer("en");
        var portuguese = new ResourceManagerPowerLinqLocalizer("pt");

        Assert.Equal("Sequence contains no elements.", english.Get("SequenceEmpty"));
        Assert.Equal("A sequência não contém elementos.", portuguese.Get("SequenceEmpty"));
    }
}
