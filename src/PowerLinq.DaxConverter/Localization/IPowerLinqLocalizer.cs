using System.Globalization;

namespace PowerLinq.DaxConverter.Localization;

/// <summary>Source of the messages the library shows to whoever uses it.</summary>
/// <remarks>
/// Every visible message lives in a <c>.resx</c>, never as a literal in code, so the language is a
/// choice of the consumer — documentation is for whoever maintains the library, a message is for
/// whoever uses it.
/// </remarks>
public interface IPowerLinqLocalizer
{
    /// <summary>The resolved culture, also used when formatting the arguments.</summary>
    CultureInfo Culture { get; }

    /// <summary>
    /// The message for the key. A missing key returns <b>the key itself</b> instead of throwing:
    /// an error message must not become a second error on the path that handles the first.
    /// </summary>
    string Get(string key);

    /// <summary>The message for the key with the arguments applied, in the resolved culture.</summary>
    string Format(string key, params object?[] arguments);
}
