using System.Globalization;
using System.Resources;

namespace PowerLinq.DaxConverter.Localization;

/// <summary>
/// <see cref="IPowerLinqLocalizer"/> over a <see cref="ResourceManager"/>, falling back to the
/// invariant catalog and from there to the key itself.
/// </summary>
public sealed class ResourceManagerPowerLinqLocalizer : IPowerLinqLocalizer
{
    private static readonly ResourceManager LibraryResources = new(
        "PowerLinq.DaxConverter.Localization.Resources.Messages",
        typeof(ResourceManagerPowerLinqLocalizer).Assembly);

    private readonly ResourceManager _resources;

    /// <summary>
    /// Shared English instance. It is the default of every overload that does not take a
    /// localizer, so the library works with no configuration at all.
    /// </summary>
    public static IPowerLinqLocalizer English { get; } = new ResourceManagerPowerLinqLocalizer("en");

    /// <summary>Localizer over the library's catalog, in the given language.</summary>
    public ResourceManagerPowerLinqLocalizer(string? language)
        : this(LibraryResources, language) { }

    /// <summary>
    /// The same culture resolution and fallback chain, over a host catalog — so the application
    /// can translate its own messages through this same system, without mixing them with the
    /// library's.
    /// </summary>
    public ResourceManagerPowerLinqLocalizer(ResourceManager resources, string? language)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        Culture = ResolveCulture(language);
    }

    /// <inheritdoc/>
    public CultureInfo Culture { get; }

    /// <inheritdoc/>
    public string Get(string key) =>
        _resources.GetString(key, Culture)
        ?? _resources.GetString(key, CultureInfo.InvariantCulture)
        ?? key;

    /// <inheritdoc/>
    public string Format(string key, params object?[] arguments) =>
        string.Format(Culture, Get(key), arguments);

    /// <summary>
    /// Resolves the language onto one of the cultures that have a catalog, falling back to
    /// <c>en</c> for everything else.
    /// </summary>
    /// <remarks>
    /// Any Portuguese variant becomes <c>pt-BR</c>, which is the catalog that exists. An invalid
    /// culture name falls back to English instead of propagating the
    /// <see cref="CultureNotFoundException"/>: a misconfigured language must not stop the
    /// application from starting.
    /// </remarks>
    private static CultureInfo ResolveCulture(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return CultureInfo.GetCultureInfo("en");

        try
        {
            var requested = CultureInfo.GetCultureInfo(language);
            return requested.TwoLetterISOLanguageName.Equals("pt", StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.GetCultureInfo("pt-BR")
                : requested.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase)
                    ? CultureInfo.GetCultureInfo("en")
                    : CultureInfo.GetCultureInfo("en");
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("en");
        }
    }
}
