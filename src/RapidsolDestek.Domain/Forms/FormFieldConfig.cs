using System.Text.Json;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Domain.Forms;

/// <summary>
/// Typed view over <see cref="FormField.Configuration"/> — the per-type JSON the S7
/// form designer writes and the portal/agent open pages consume (osTicket field
/// configuration parity, flat v1 schema):
///   list_id    int    — "choices" backed by a custom list (admin/lists)
///   choices    [str]  — inline "choices" options (one per designer textarea line)
///   default    str    — pre-filled value (admin/form-edit ⚙ dialog)
///   validation str    — "email" | "phone" | "number" format check
/// Unknown keys are preserved nowhere — the designer owns the whole document.
/// </summary>
public sealed record FormFieldConfig(
    int? ListId,
    IReadOnlyList<string> Choices,
    string? Default,
    string? Validation)
{
    public static readonly FormFieldConfig Empty = new(null, [], null, null);

    public static FormFieldConfig Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Empty;

            int? listId = doc.RootElement.TryGetProperty("list_id", out var idEl)
                && idEl.TryGetInt32(out var id) && id > 0 ? id : null;

            var choices = new List<string>();
            if (doc.RootElement.TryGetProperty("choices", out var chEl)
                && chEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in chEl.EnumerateArray())
                {
                    if (c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } s)
                        choices.Add(s);
                }
            }

            string? S(string key) =>
                doc.RootElement.TryGetProperty(key, out var el)
                && el.ValueKind == JsonValueKind.String
                && el.GetString() is { Length: > 0 } v ? v : null;

            return new FormFieldConfig(listId, choices, S("default"), S("validation"));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>Serialize back to the flat JSON document (null = nothing configured).</summary>
    public string? ToJson()
    {
        var map = new Dictionary<string, object>();
        if (ListId is { } listId)
            map["list_id"] = listId;
        else if (Choices.Count > 0)
            map["choices"] = Choices;
        if (!string.IsNullOrWhiteSpace(Default))
            map["default"] = Default;
        if (Validation is "email" or "phone" or "number")
            map["validation"] = Validation;
        return map.Count == 0 ? null : JsonSerializer.Serialize(map);
    }

    /// <summary>Format check for a non-empty posted value (⚙ dialog "Doğrulama";
    /// both open pages enforce it server-side).</summary>
    public bool IsValidValue(string value) => Validation switch
    {
        "email" => System.Text.RegularExpressions.Regex.IsMatch(
            value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"),
        "phone" => System.Text.RegularExpressions.Regex.IsMatch(
            value, @"^\+?[0-9 ()\-]{7,20}$"),
        "number" => double.TryParse(value,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _),
        _ => true,
    };
}
