using System.Globalization;
using System.Text.Json;
using OpenID.MCPInterop.Client.Rendering;

namespace OpenID.MCPInterop.Client.Helpers;

/// <summary>
/// Builds the CallToolAsync arguments dictionary from the Fields-mode form
/// post: one text value per schema property, converted per its declared type
/// (object/array fields expect raw JSON). Blank, non-required fields are
/// omitted rather than sent as empty strings. Anything the schema says the
/// server would reject - a missing required field, a non-numeric number, a
/// value outside an <c>enum</c> - is rejected locally instead, so the log
/// explains the problem rather than relaying an opaque server error.
/// </summary>
internal static class ToolArgumentBuilder
{
    /// <exception cref="FormatException">A submitted value doesn't satisfy the field's schema.</exception>
    public static Dictionary<string, object?> Build(IReadOnlyDictionary<string, string> values, IReadOnlyList<ClientHtmlRenderer.SchemaField> fields)
    {
        var arguments = new Dictionary<string, object?>();
        foreach (var field in fields)
        {
            var raw = values.TryGetValue(field.Name, out var value) ? value.Trim() : string.Empty;
            if (raw.Length == 0)
            {
                if (field.Required)
                {
                    throw new FormatException($"argument '{field.Name}' is required");
                }

                continue;
            }

            if (field.EnumValues is { Count: > 0 } allowed && !allowed.Contains(raw))
            {
                throw new FormatException($"argument '{field.Name}' must be one of: {string.Join(", ", allowed)}");
            }

            arguments[field.Name] = Convert(field, raw);
        }

        return arguments;
    }

    private static object Convert(ClientHtmlRenderer.SchemaField field, string raw) => field.Type switch
    {
        "integer" => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
            ? integer
            : throw new FormatException($"argument '{field.Name}' must be a whole number, got '{raw}'"),
        "number" => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new FormatException($"argument '{field.Name}' must be a number, got '{raw}'"),
        "boolean" => bool.TryParse(raw, out var boolean)
            ? boolean
            : throw new FormatException($"argument '{field.Name}' must be true or false, got '{raw}'"),
        "object" or "array" => ParseJson(field, raw),
        _ => raw,
    };

    private static JsonElement ParseJson(ClientHtmlRenderer.SchemaField field, string raw)
    {
        JsonElement element;
        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"argument '{field.Name}' must be valid JSON: {ex.Message}", ex);
        }

        var expectedKind = field.Type == "object" ? JsonValueKind.Object : JsonValueKind.Array;
        if (element.ValueKind != expectedKind)
        {
            throw new FormatException($"argument '{field.Name}' must be a JSON {field.Type}, got {element.ValueKind.ToString().ToLowerInvariant()}");
        }

        return element;
    }
}
