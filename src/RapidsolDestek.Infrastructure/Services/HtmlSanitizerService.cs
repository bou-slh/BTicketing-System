using System.Text.RegularExpressions;
using Ganss.Xss;

namespace RapidsolDestek.Infrastructure.Services;

public interface IHtmlSanitizerService
{
    /// <summary>Allowlist-sanitizes user/agent HTML before it is stored.</summary>
    string Sanitize(string html);

    /// <summary>Tag-stripped plain text for excerpts and search previews.</summary>
    string ToPlainText(string html);
}

/// <summary>
/// Single ingress choke point for thread HTML (osTicket Format::sanitize parity).
/// Allowlist mirrors the markup the mockup composers can produce; scripts, event
/// handlers, iframes and non-http(s)/cid URLs are stripped. Registered as a
/// singleton — <see cref="HtmlSanitizer"/> is thread-safe for Sanitize calls.
/// </summary>
public sealed partial class HtmlSanitizerService : IHtmlSanitizerService
{
    private readonly HtmlSanitizer _sanitizer;

    public HtmlSanitizerService()
    {
        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowedTags.Clear();
        foreach (var tag in new[]
        {
            "p", "br", "div", "span", "blockquote", "pre", "code",
            "ul", "ol", "li", "b", "strong", "i", "em", "u", "s",
            "a", "img", "h1", "h2", "h3", "h4",
            "table", "thead", "tbody", "tr", "th", "td", "hr",
        })
        {
            _sanitizer.AllowedTags.Add(tag);
        }

        _sanitizer.AllowedAttributes.Clear();
        foreach (var attr in new[] { "href", "src", "alt", "title", "colspan", "rowspan" })
            _sanitizer.AllowedAttributes.Add(attr);

        _sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto", "cid" })
            _sanitizer.AllowedSchemes.Add(scheme);

        _sanitizer.AllowedCssProperties.Clear();
        _sanitizer.KeepChildNodes = true;
    }

    public string Sanitize(string html) =>
        string.IsNullOrEmpty(html) ? "" : _sanitizer.Sanitize(html);

    public string ToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html))
            return "";
        var text = TagPattern().Replace(html, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return WhitespacePattern().Replace(text, " ").Trim();
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
