using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerLinq.DaxConverter.Model;

/// <summary>
/// The <see cref="DaxModelSchema"/> as a file — the artifact that gets versioned in the repository.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is what separates reading from consuming.</b> Contract generation has to preserve the
/// property that composition touches no network; a <i>source generator</i>, on top of that,
/// <b>cannot</b> do network I/O at compile time. Both constraints point at the same design:
/// someone reads the model once and writes the schema; generation and validation read the file.
/// </para>
/// <para>
/// Versioning the file has an effect that is worth having on purpose: a change to the model shows
/// up in the <c>git diff</c>. A column the BI team removed stops being an error that surfaces in
/// production and becomes a red line in review.
/// </para>
/// <para>
/// The <see cref="FormatVersion"/> is written and checked on read. A file of a future format is
/// refused while naming both versions: interpreting it with the old reader would produce a
/// <b>partial</b> schema, and a partial schema turns into invented divergence — the tool would
/// report a column the model does have as missing.
/// </para>
/// </remarks>
public static class DaxSchemaFile
{
    /// <summary>The format version this code writes and knows how to read.</summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,

        // The file goes into version control, so the order and the shape have to be stable: a
        // serializer that reordered properties would produce a diff on every read of the model.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The schema as JSON.</summary>
    /// <param name="schema">The schema.</param>
    public static string Write(DaxModelSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        return JsonSerializer.Serialize(
            new Document(FormatVersion, schema.Tables, schema.Measures, schema.Relationships),
            Options);
    }

    /// <summary>The schema from JSON.</summary>
    /// <param name="json">The file's contents.</param>
    /// <exception cref="InvalidOperationException">
    /// The JSON is not a schema, or is of a format this code does not know how to read.
    /// </exception>
    public static DaxModelSchema Read(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        Document? document = JsonSerializer.Deserialize<Document>(json, Options)
                             ?? throw new InvalidOperationException(
                                 "This is not a PowerLinq model schema: the JSON deserialized to null.");

        if (document.FormatVersion != FormatVersion)
        {
            throw new InvalidOperationException(
                $"This schema file is format version {document.FormatVersion}, and this build reads "
                + $"version {FormatVersion}. Reading it anyway would produce a partial schema, and a "
                + "partial schema reports columns the model does have as missing. Regenerate the "
                + "file, or update the package.");
        }

        return new DaxModelSchema(
            document.Tables ?? [],
            document.Measures ?? [],
            document.Relationships ?? []);
    }
}
