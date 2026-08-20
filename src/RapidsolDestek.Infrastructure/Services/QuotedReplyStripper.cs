using System.Text.RegularExpressions;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// Quoted-reply removal for inbound mail (email/strip_quoted + reply_separator,
/// osTicket ThreadEntryBody::stripQuotedReply modeled). Deliberately conservative:
/// the separator tag cut is osTicket's exact semantic; the marker/quote-tail rules
/// are documented heuristics, and a cut that would leave nothing keeps the original
/// body (osTicket parity — a stripped-empty message falls back to the full text).
/// </summary>
public static partial class QuotedReplyStripper
{
    [GeneratedRegex(@"^-{2,}\s*(Original Message|Orijinal Mesaj|Özgün İleti)\s*-{2,}\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex OriginalMessageMarker();

    /// <summary>Attribution line directly above a quoted block ("On … wrote:" /
    /// "… tarihinde … yazdı:" / "… şunu yazdı:").</summary>
    [GeneratedRegex(@"(wrote:|yazdı:|şunu yazmıştı:)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AttributionLine();

    [GeneratedRegex(@"<(blockquote|div\s+class=(""|')?gmail_quote)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlQuoteStart();

    /// <summary>Plain-text strip: separator tag cut, else "Original Message" marker,
    /// else the trailing "&gt;"-quoted block (with its attribution line).</summary>
    public static string StripText(string text, string? separator)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        if (!string.IsNullOrWhiteSpace(separator))
        {
            var at = text.IndexOf(separator, StringComparison.Ordinal);
            if (at >= 0 && text[..at].Trim().Length > 0)
                return text[..at].TrimEnd();
        }

        var marker = OriginalMessageMarker().Match(text);
        if (marker.Success && text[..marker.Index].Trim().Length > 0)
            return text[..marker.Index].TrimEnd();

        // Trailing quoted block: from the last non-">" content downwards everything
        // is ">"-prefixed (blank lines allowed) — drop it and a preceding attribution.
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var cut = lines.Length;
        while (cut > 0 && (lines[cut - 1].TrimStart().StartsWith('>') || lines[cut - 1].Trim().Length == 0))
            cut--;
        if (cut < lines.Length && lines[cut..].Any(l => l.TrimStart().StartsWith('>')))
        {
            if (cut > 0 && AttributionLine().IsMatch(lines[cut - 1].TrimEnd()))
                cut--;
            var kept = string.Join("\n", lines[..cut]).TrimEnd();
            if (kept.Trim().Length > 0)
                return kept;
        }
        return text;
    }

    /// <summary>HTML strip: separator tag cut (unbalanced tags are rebalanced by the
    /// ingress sanitizer), else the first gmail_quote div / blockquote when the body
    /// effectively ends with it (a mid-body quote the author replied under is kept).</summary>
    public static string StripHtml(string html, string? separator)
    {
        if (string.IsNullOrWhiteSpace(html))
            return html;

        if (!string.IsNullOrWhiteSpace(separator))
        {
            var at = html.IndexOf(separator, StringComparison.Ordinal);
            if (at >= 0 && HasVisibleText(html[..at]))
                return html[..at];
        }

        var quote = HtmlQuoteStart().Match(html);
        if (quote.Success && !HasVisibleText(html[TailAfterQuote(html, quote.Index)..])
            && HasVisibleText(html[..quote.Index]))
        {
            return html[..quote.Index];
        }
        return html;
    }

    /// <summary>End index of the quote block starting at <paramref name="start"/> —
    /// naive last close-tag scan; only used to test whether real text follows.</summary>
    private static int TailAfterQuote(string html, int start)
    {
        var lastClose = html.LastIndexOf("</blockquote>", StringComparison.OrdinalIgnoreCase);
        return lastClose > start ? lastClose + "</blockquote>".Length : html.Length;
    }

    private static bool HasVisibleText(string htmlFragment) =>
        Regex.Replace(htmlFragment, "<[^>]*>", " ").Trim().Length > 0;
}
