using System.Text.RegularExpressions;

namespace RapidsolDestek.Domain.Services;

/// <summary>
/// Expands osTicket-style %{variable.path} placeholders against a flat bag
/// (osTicket VariableReplacer parity, flattened). Unknown variables expand to an
/// empty string — deterministic output, never a leaked placeholder. Shared by
/// canned responses (S4) and email templates (S8).
/// </summary>
public static partial class TemplateVariableExpander
{
    [GeneratedRegex(@"%\{([A-Za-z0-9_.]+)\}")]
    private static partial Regex Placeholder();

    public static string Expand(string template, IReadOnlyDictionary<string, string?> variables)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        return Placeholder().Replace(template, m =>
            variables.TryGetValue(m.Groups[1].Value, out var value) ? value ?? "" : "");
    }

    /// <summary>Variable names referenced by a template (for the S7 variable-pill UI).</summary>
    public static IReadOnlyList<string> ListVariables(string template) =>
        [.. Placeholder().Matches(template).Select(m => m.Groups[1].Value).Distinct()];
}
