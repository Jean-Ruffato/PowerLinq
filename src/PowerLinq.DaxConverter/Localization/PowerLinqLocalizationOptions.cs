namespace PowerLinq.DaxConverter.Localization;

/// <summary>Message language, bound to the <c>PowerLinq:Localization</c> configuration section.</summary>
public sealed class PowerLinqLocalizationOptions
{
    /// <summary>Path of the section in the configuration.</summary>
    public const string SectionName = "PowerLinq:Localization";

    /// <summary>
    /// Requested language. <c>en</c>, <c>pt</c> and <c>pt-BR</c> are served; any other value,
    /// invalid ones included, falls back to <c>en</c>.
    /// </summary>
    public string Language { get; set; } = "en";
}
