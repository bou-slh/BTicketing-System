using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// Snapshot of an incoming ticket the filters run over (osTicket's vars array).
/// Field keys mirror osTicket filter_rule.what: name/email/replyto/subject/body/
/// topic/org/source. Reply-To only exists for mail — it stays null on the create
/// channels that exist today (TODO(S8): the mail pipeline fills it).
/// </summary>
public sealed record FilterInput
{
    public TicketSource Source { get; init; } = TicketSource.Web;
    public int? EmailAccountId { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? ReplyTo { get; init; }
    public string? Subject { get; init; }
    public string? Body { get; init; }
    public string? TopicName { get; init; }
    public string? OrgName { get; init; }
}

/// <summary>
/// Aggregate of every matching filter's actions, applied in exec order (later
/// matches overwrite earlier values, osTicket parity). A Reject action halts the
/// run immediately. Live consumers: TicketService.CreateAsync routing overrides,
/// the AutoResponseDisabled ticket flag and internal Note entries. Canned/send-
/// email actions have no consumer until the mail subsystem — TODO(S8).
/// </summary>
public sealed class FilterOutcome
{
    public List<string> MatchedFilters { get; } = [];
    public string? RejectedBy { get; set; }
    public int? DepartmentId { get; set; }
    public int? PriorityId { get; set; }
    public int? SlaId { get; set; }
    public int? StatusId { get; set; }
    public int? TopicId { get; set; }
    public int? StaffId { get; set; }
    public int? TeamId { get; set; }
    public bool DisableAutoResponse { get; set; }
    public List<string> Notes { get; } = [];
}

/// <summary>Match-count preview payload (admin/filter-edit, ROADMAP B4 "N tickets
/// would match"). MailOnlyRules counts rules that reference mail-header fields
/// (reply-to) which cannot be evaluated against stored tickets — they are skipped
/// from the evaluation and reported honestly.</summary>
public sealed record FilterPreviewResult(int Matches, int Total, int MailOnlyRules);

public interface IFilterEngine
{
    /// <summary>Runs every active filter (exec order, target-gated) over the
    /// incoming ticket data and folds the matching filters' actions.</summary>
    Task<FilterOutcome> RunAsync(FilterInput input, CancellationToken ct = default);

    /// <summary>Evaluates the CURRENT (possibly unsaved) rule set against the
    /// existing ticket store and returns how many tickets would match.</summary>
    Task<FilterPreviewResult> PreviewAsync(
        IReadOnlyList<FilterRule> rules, bool matchAll, CancellationToken ct = default);
}

/// <summary>
/// Ticket-filter engine (mockups/admin/filters.html, osTicket class Filter/
/// TicketFilter). The pure matcher is static so rule semantics are testable
/// without a database. String operators compare case-insensitively with ordinal
/// (culture-invariant) semantics — the Turkish dotted 'İ' is NOT folded onto 'i'
/// (documented deviation from TR-culture casing; keeps matching locale-stable).
/// Regex rules run the pattern verbatim (case-sensitive; authors can opt out via
/// (?i)) with a timeout — invalid patterns and timeouts count as "no match".
/// </summary>
public sealed class FilterEngine(AppDbContext db) : IFilterEngine
{
    /// <summary>Rule fields that only exist on inbound mail — they can never match
    /// a stored ticket, so the preview skips and reports them.</summary>
    public static readonly IReadOnlySet<string> MailOnlyWhats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "replyto" };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public async Task<FilterOutcome> RunAsync(FilterInput input, CancellationToken ct = default)
    {
        var filters = await db.Filters.AsNoTracking()
            .Where(f => f.IsActive)
            .Include(f => f.Rules)
            .Include(f => f.Actions)
            .OrderBy(f => f.ExecOrder).ThenBy(f => f.Id)
            .ToListAsync(ct);

        var outcome = new FilterOutcome();
        foreach (var filter in filters)
        {
            if (!TargetApplies(filter, input) || !Matches(filter, input))
                continue;

            outcome.MatchedFilters.Add(filter.Name);
            foreach (var action in filter.Actions.OrderBy(a => a.Sort).ThenBy(a => a.Id))
            {
                Apply(outcome, action, filter.Name);
                if (outcome.RejectedBy is not null)
                    return outcome; // reject halts the whole run (osTicket parity)
            }

            if (filter.StopOnMatch)
                break;
        }
        return outcome;
    }

    public async Task<FilterPreviewResult> PreviewAsync(
        IReadOnlyList<FilterRule> rules, bool matchAll, CancellationToken ct = default)
    {
        var evaluable = rules.Where(r => !MailOnlyWhats.Contains(r.What)).ToList();
        var mailOnly = rules.Count - evaluable.Count;
        var total = await db.Tickets.CountAsync(ct);
        if (evaluable.Count == 0)
            return new FilterPreviewResult(0, total, mailOnly);

        // Body rules read the thread's first end-user Message — only joined when
        // a rule actually asks for it.
        var needsBody = evaluable.Any(r => string.Equals(r.What, "body", StringComparison.OrdinalIgnoreCase));

        var rows = await db.Tickets.AsNoTracking()
            .Select(t => new FilterInput
            {
                Source = t.Source,
                EmailAccountId = t.EmailAccountId,
                Subject = t.Subject,
                Name = t.User!.Name,
                Email = t.User!.Emails
                    .Where(e => e.Id == (t.UserEmailId ?? t.User!.DefaultEmailId))
                    .Select(e => e.Address).FirstOrDefault(),
                OrgName = t.User!.Organization != null ? t.User!.Organization.Name : null,
                TopicName = t.HelpTopic != null ? t.HelpTopic.Name : null,
                Body = needsBody
                    ? t.Thread!.Entries
                        .Where(e => e.Type == ThreadEntryType.Message)
                        .OrderBy(e => e.Id).Select(e => e.Body).FirstOrDefault()
                    : null,
            })
            .ToListAsync(ct);

        var matches = rows.Count(row => matchAll
            ? evaluable.All(rule => RuleMatches(rule, row))
            : evaluable.Any(rule => RuleMatches(rule, row)));
        return new FilterPreviewResult(matches, total, mailOnly);
    }

    // ---- pure matcher --------------------------------------------------------------------

    /// <summary>Channel gate: the filter's target must cover the incoming source;
    /// an email-account restriction (osTicket email_id) additionally pins the
    /// receiving mailbox. Phone/Other sources only meet target "Any".</summary>
    public static bool TargetApplies(Filter filter, FilterInput input) => filter.Target switch
    {
        FilterTarget.Any => true,
        FilterTarget.Web => input.Source == TicketSource.Web,
        FilterTarget.Api => input.Source == TicketSource.Api,
        FilterTarget.Email => input.Source == TicketSource.Email
            && (filter.EmailAccountId is null || filter.EmailAccountId == input.EmailAccountId),
        _ => false,
    };

    /// <summary>Rule evaluation under the filter's AND/OR mode. A filter with no
    /// active rules matches nothing (osTicket refuses saving rule-less filters).</summary>
    public static bool Matches(Filter filter, FilterInput input)
    {
        var rules = filter.Rules.Where(r => r.IsActive).ToList();
        if (rules.Count == 0)
            return false;
        return filter.MatchAllRules
            ? rules.All(r => RuleMatches(r, input))
            : rules.Any(r => RuleMatches(r, input));
    }

    /// <summary>One rule against one input; an absent field (null) never matches —
    /// including the negated operators (osTicket parity).</summary>
    public static bool RuleMatches(FilterRule rule, FilterInput input)
    {
        var value = ValueFor(input, rule.What);
        if (value is null)
            return false;
        var expected = rule.Value;
        return rule.How switch
        {
            FilterMatchHow.Equal => value.Equals(expected, StringComparison.OrdinalIgnoreCase),
            FilterMatchHow.NotEqual => !value.Equals(expected, StringComparison.OrdinalIgnoreCase),
            FilterMatchHow.Contains => value.Contains(expected, StringComparison.OrdinalIgnoreCase),
            FilterMatchHow.NotContains => !value.Contains(expected, StringComparison.OrdinalIgnoreCase),
            FilterMatchHow.StartsWith => value.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            FilterMatchHow.EndsWith => value.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            // An invalid/timed-out pattern matches NOTHING on either polarity —
            // the negation must not turn a broken rule into an always-true one.
            FilterMatchHow.Matches => SafeRegex(value, expected) == true,
            FilterMatchHow.NotMatches => SafeRegex(value, expected) == false,
            _ => false,
        };
    }

    /// <summary>Field lookup by osTicket what-key; unknown keys match nothing.</summary>
    public static string? ValueFor(FilterInput input, string what) => what.ToLowerInvariant() switch
    {
        "name" => input.Name,
        "email" => input.Email,
        "replyto" => input.ReplyTo,
        "subject" => input.Subject,
        "body" => input.Body,
        "topic" => input.TopicName,
        "org" => input.OrgName,
        "source" => input.Source.ToString(),
        _ => null,
    };

    /// <summary>null = the pattern is invalid or timed out (undecidable).</summary>
    private static bool? SafeRegex(string value, string pattern)
    {
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.None, RegexTimeout);
        }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    // ---- action folding ------------------------------------------------------------------

    /// <summary>Folds one action into the outcome. Types mirror osTicket filter
    /// actions; configuration JSON keys follow the seed canon ({"dept_id":N}…).
    /// "canned" (send canned autoreply) and "email" (send email) persist on the
    /// filter but have no consumer until the mail subsystem — TODO(S8).</summary>
    private static void Apply(FilterOutcome outcome, FilterAction action, string filterName)
    {
        switch (action.Type)
        {
            case "reject":
                outcome.RejectedBy = filterName;
                break;
            case "dept":
                outcome.DepartmentId = ConfigId(action.Configuration, "dept_id") ?? outcome.DepartmentId;
                break;
            case "priority":
                outcome.PriorityId = ConfigId(action.Configuration, "priority_id") ?? outcome.PriorityId;
                break;
            case "sla":
                outcome.SlaId = ConfigId(action.Configuration, "sla_id") ?? outcome.SlaId;
                break;
            case "team":
                outcome.TeamId = ConfigId(action.Configuration, "team_id") ?? outcome.TeamId;
                break;
            case "agent":
                outcome.StaffId = ConfigId(action.Configuration, "staff_id") ?? outcome.StaffId;
                break;
            case "topic":
                outcome.TopicId = ConfigId(action.Configuration, "topic_id") ?? outcome.TopicId;
                break;
            case "status":
                outcome.StatusId = ConfigId(action.Configuration, "status_id") ?? outcome.StatusId;
                break;
            case "noautoresp":
                outcome.DisableAutoResponse = true;
                break;
            case "note":
                if (ConfigString(action.Configuration, "note") is { Length: > 0 } note)
                    outcome.Notes.Add(note);
                break;
            // "canned", "email": persisted-only until S8 (no outbound mail yet).
        }
    }

    private static int? ConfigId(string? json, string key)
    {
        if (TryGetProperty(json, key, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n))
                return n;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var s))
                return s;
        }
        return null;
    }

    private static string? ConfigString(string? json, string key) =>
        TryGetProperty(json, key, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool TryGetProperty(string? json, string key, out JsonElement property)
    {
        property = default;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(key, out var found))
                return false;
            property = found.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
