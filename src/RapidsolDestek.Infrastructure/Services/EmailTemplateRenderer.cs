using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Services;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>Rendered template: substituted plain-text subject + HTML body.</summary>
public sealed record EmailRenderResult(string Subject, string HtmlBody);

/// <summary>
/// Context for one render. Plain values are HTML-encoded into the body;
/// <see cref="MessageHtml"/>/<see cref="ResponseHtml"/> are content variables —
/// already-sanitized HTML injected raw as %{message} / %{response}.
/// </summary>
public sealed record EmailRenderContext
{
    public int? TicketId { get; init; }

    public string? RecipientName { get; init; }

    public string? RecipientEmail { get; init; }

    /// <summary>%{message} — sanitized HTML (thread entry body), injected raw.</summary>
    public string? MessageHtml { get; init; }

    /// <summary>%{response} — sanitized HTML (staff reply body), injected raw.</summary>
    public string? ResponseHtml { get; init; }

    /// <summary>Caller-specific plain-value overrides/additions (merged last).</summary>
    public IReadOnlyDictionary<string, string?>? Extra { get; init; }
}

/// <summary>
/// Renders one <see cref="EmailTemplateCatalog"/> template (subject + HTML body),
/// substituting %{variable} placeholders per osTicket semantics
/// (include/class.variable.php referenced for meaning only). Template row comes from
/// the configured default set (email/default_template_set_id; 0 = active "tr" set —
/// the EffortEmailHandler selection, now owned here). Unknown variables render empty
/// (TemplateVariableExpander), never throw. Variable roots: %{ticket.*} (number,
/// subject, status, priority, dept, topic, owner, assignee, link via core/
/// helpdesk_url…), %{recipient.*}, %{staff.*} (assigned agent), %{company.*}
/// (company/* settings), %{effort.*} (latest proposal), %{message}/%{response}.
/// </summary>
public interface IEmailTemplateRenderer
{
    /// <summary>Null when the template row is missing from the configured set
    /// (caller logs and skips — EffortEmailHandler precedent).</summary>
    Task<EmailRenderResult?> RenderAsync(string templateCode, EmailRenderContext context, CancellationToken ct = default);

    /// <summary>The raw (unencoded) substitution bag for a context — exposed for
    /// tests and future composers (e.g. reply-template prefill).</summary>
    Task<IReadOnlyDictionary<string, string?>> BuildVariablesAsync(EmailRenderContext context, CancellationToken ct = default);
}

public sealed class EmailTemplateRenderer(AppDbContext db, ISettingsService settings) : IEmailTemplateRenderer
{
    /// <summary>Content variables: already-sanitized HTML, never re-encoded.</summary>
    private static readonly string[] ContentKeys = ["message", "response"];

    /// <summary>Encodes markup characters only — Turkish text stays literal in mail
    /// bodies (WebUtility would numeric-encode every non-ASCII character).</summary>
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    public async Task<EmailRenderResult?> RenderAsync(string templateCode, EmailRenderContext context, CancellationToken ct = default)
    {
        // LIVE email-settings consumer (admin/email-settings "Varsayılan şablon
        // seti"): render from the configured set; 0 keeps the pre-S7 behavior
        // (the active "tr" set). Seeded canon set wins among several active ones.
        var setId = (await settings.GetEmailAsync(ct)).DefaultTemplateSetId;
        var template = await db.EmailTemplateSets
            .Where(s => setId > 0 ? s.Id == setId : s.IsActive && s.Language == "tr")
            .OrderBy(s => s.Id)
            .SelectMany(s => s.Templates)
            .Where(t => t.CodeName == templateCode)
            .FirstOrDefaultAsync(ct);
        if (template is null)
            return null;

        var raw = await BuildVariablesAsync(context, ct);

        // Subject is plain text — raw values. Body is HTML — encode every plain
        // value; content variables (%{message}/%{response}) pass through as-is.
        var encoded = raw.ToDictionary(
            p => p.Key,
            p => ContentKeys.Contains(p.Key) || p.Value is null ? p.Value : Encoder.Encode(p.Value));
        return new EmailRenderResult(
            TemplateVariableExpander.Expand(template.Subject, raw),
            TemplateVariableExpander.Expand(template.Body, encoded));
    }

    public async Task<IReadOnlyDictionary<string, string?>> BuildVariablesAsync(EmailRenderContext context, CancellationToken ct = default)
    {
        var bag = new Dictionary<string, string?>
        {
            ["recipient.name"] = context.RecipientName,
            ["recipient.email"] = context.RecipientEmail,
            ["message"] = context.MessageHtml,
            ["response"] = context.ResponseHtml,
        };

        // %{company.*} from company/* settings (admin/settings-company; fallbacks =
        // the page's canonical sample data, so mails match what the admin page shows).
        var company = await settings.GetSectionAsync("company", ct);
        bag["company.name"] = company.GetValueOrDefault("name", "Rapidsol Bilişim ve Danışmanlık A.Ş.");
        bag["company.website"] = company.GetValueOrDefault("website", "https://www.rapidsol.com.tr");
        bag["company.phone"] = company.GetValueOrDefault("phone", "+90 212 555 24 24");
        bag["company.address"] = company.GetValueOrDefault("address", "Maslak Mah. Büyükdere Cad. No:245 K:11\nSarıyer / İstanbul");

        if (context.TicketId is { } ticketId)
        {
            var ticket = await db.Tickets
                .Include(t => t.User!).ThenInclude(u => u.Emails)
                .Include(t => t.Department)
                .Include(t => t.Staff)
                .Include(t => t.Team)
                .Include(t => t.Status)
                .Include(t => t.Priority)
                .Include(t => t.HelpTopic)
                .AsSplitQuery()
                .AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == ticketId, ct)
                ?? throw new DomainNotFoundException("Ticket", ticketId);

            var ownerEmail = ticket.User?.Emails.FirstOrDefault(e => e.Id == ticket.User!.DefaultEmailId)?.Address
                ?? ticket.User?.Emails.FirstOrDefault()?.Address;

            // Link base = core/helpdesk_url (admin/settings-system "Yardım masası
            // URL'si"); default mirrors the settings page fallback.
            var baseUrl = (await settings.GetAsync("core", "helpdesk_url", ct)
                ?? "https://destek.rapidsol.com.tr/").TrimEnd('/');
            var link = $"{baseUrl}/ticket-view?id={ticket.Id}";

            bag["ticket.number"] = ticket.Number;
            bag["ticket.subject"] = ticket.Subject;
            bag["ticket.status"] = ticket.Status?.Name;
            bag["ticket.priority"] = ticket.Priority?.Name;
            bag["ticket.dept"] = ticket.Department?.Name;
            bag["ticket.dept.name"] = ticket.Department?.Name;
            bag["ticket.topic"] = ticket.HelpTopic?.Name;
            bag["ticket.topic.name"] = ticket.HelpTopic?.Name;
            bag["ticket.name"] = ticket.User?.Name;          // osTicket: owner name
            bag["ticket.email"] = ownerEmail;                // osTicket: owner email
            bag["ticket.user.name"] = ticket.User?.Name;
            bag["ticket.user.email"] = ownerEmail;
            bag["ticket.staff.name"] = ticket.Staff?.FullName;
            bag["ticket.staff.email"] = ticket.Staff?.Email;
            bag["ticket.assignee"] = ticket.Staff?.FullName ?? ticket.Team?.Name;
            bag["ticket.source"] = ticket.Source.ToString();
            bag["ticket.create_date"] = Stamp(ticket.CreatedAt);
            bag["ticket.due_date"] = Stamp(ticket.DueDate ?? ticket.EstimatedDueDate);
            bag["ticket.close_date"] = Stamp(ticket.ClosedAt);
            bag["ticket.link"] = link;
            bag["recipient.ticket_link"] = link;

            // %{staff.*} = the assigned agent (override via Extra when the sender
            // differs — e.g. a responding agent on reply alerts, later slices).
            bag["staff.name"] = ticket.Staff?.FullName;
            bag["staff.email"] = ticket.Staff?.Email;

            var effort = await db.EffortProposals
                .Where(p => p.TicketId == ticketId)
                .OrderByDescending(p => p.RevisionNo)
                .FirstOrDefaultAsync(ct);
            bag["effort.hours"] = effort?.Hours.ToString("0.##");
            bag["effort.note"] = effort?.Note;
            bag["effort.revision"] = effort?.RevisionNo.ToString();
        }

        if (context.Extra is not null)
        {
            foreach (var (key, value) in context.Extra)
                bag[key] = value;
        }
        return bag;
    }

    /// <summary>CannedResponseService's stamp format — one canon for mail dates.</summary>
    private static string? Stamp(DateTimeOffset? at) => at?.ToString("dd.MM.yyyy HH:mm");
}
