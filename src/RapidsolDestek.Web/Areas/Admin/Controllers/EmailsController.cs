using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin email addresses list + editor (mockups/admin/emails.html + email-edit.html,
/// ROADMAP §6.3). List: the B1 engine over EmailAccount rows with toolbar bulk
/// enable/disable/delete (enable/disable flips the account's channel IsActive flags;
/// delete guard: accounts referenced by departments, filters or the email settings
/// are skipped — teams precedent). Editor: account identity + new-ticket routing,
/// the incoming Mailbox and outgoing Smtp <see cref="EmailChannel"/>s with B3 gating
/// (fetch/SMTP status radios gate their sections, the protocol select gates the
/// folder field, the after-fetch select gates the archive folder), per-protocol
/// credential dialogs with Basic/OAuth2 tabs (B2 — the mockup shares one dlg-auth;
/// split per channel, canned/teams precedent, flagged), write-only secrets
/// (DataProtection columns + bullet sentinel), and the REAL MailKit
/// "Bağlantıyı Sına" probe over the posted, unsaved settings (invented UI, flagged
/// — the ROADMAP row names Test connection; the mockup defines no control).
/// The fetch/send pipeline itself is S8 — these are its configuration surfaces.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class EmailsController(
    AppDbContext db,
    ISettingsService settings,
    IEmailSecretProtector secrets,
    IMailConnectionTester tester) : Controller
{
    public const int PageSize = 8;

    /// <summary>Write-only secret sentinels (mockup bullet counts): a posted value
    /// that is empty or bullets-only means "leave the stored secret unchanged".</summary>
    public const string PasswordSentinel = "••••••••••";
    public const string ClientSecretSentinel = "••••••••••••••••";

    private static readonly string[] SortKeys = ["email", "priority", "dept", "updated"];

    // ---- emails.html (B1 list) ---------------------------------------------------------

    [HttpGet("/admin/emails")]
    [NavKey("emails")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var query = db.EmailAccounts.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(a => EF.Functions.ILike(a.Address, pattern)
                || (a.DisplayName != null && EF.Functions.ILike(a.DisplayName, pattern)));
        }

        var projected = query.Select(a => new
        {
            a.Id, a.Address, a.DisplayName,
            PriorityKey = db.TicketPriorities.Where(p => p.Id == a.PriorityId).Select(p => p.Key).FirstOrDefault(),
            // Lower urgency value = more urgent (priority sort follows severity).
            PriorityUrgency = db.TicketPriorities.Where(p => p.Id == a.PriorityId).Select(p => (int?)p.Urgency).FirstOrDefault(),
            DeptName = db.Departments.Where(d => d.Id == a.DepartmentId).Select(d => d.Name).FirstOrDefault(),
            Updated = a.UpdatedAt ?? a.CreatedAt,
        });

        // The mockup's sort indicator sits on the E-posta column (sorted-desc).
        var sortKey = SortKeys.Contains(sort) ? sort! : "email";
        var desc = dir == "asc" ? false : dir == "desc" || sortKey is "email" or "updated";
        projected = (sortKey, desc) switch
        {
            ("priority", true) => projected.OrderBy(a => a.PriorityUrgency == null).ThenBy(a => a.PriorityUrgency).ThenBy(a => a.Address),
            ("priority", false) => projected.OrderBy(a => a.PriorityUrgency == null).ThenByDescending(a => a.PriorityUrgency).ThenBy(a => a.Address),
            ("dept", true) => projected.OrderByDescending(a => a.DeptName ?? "").ThenBy(a => a.Id),
            ("dept", false) => projected.OrderBy(a => a.DeptName ?? "").ThenBy(a => a.Id),
            ("updated", true) => projected.OrderByDescending(a => a.Updated).ThenByDescending(a => a.Id),
            ("updated", false) => projected.OrderBy(a => a.Updated).ThenBy(a => a.Id),
            (_, true) => projected.OrderByDescending(a => a.Address),
            _ => projected.OrderBy(a => a.Address),
        };

        var total = await projected.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await projected.Skip((page - 1) * PageSize).Take(PageSize)
            .Select(a => new EmailRowVm(a.Id, a.Address, a.DisplayName, a.PriorityKey, a.DeptName, a.Updated))
            .ToListAsync(ct);

        return View(new EmailsIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), rows));
    }

    /// <summary>
    /// Toolbar bulk actions over the selection. Enable/disable flips IsActive on the
    /// account's existing channels (fetch + SMTP status — the account itself carries
    /// no active flag, osTicket parity; interpretation flagged for canon). Delete
    /// guard: accounts referenced by departments (outgoing/auto-response address),
    /// filters or the email settings (default/alert/default-SMTP) are skipped.
    /// </summary>
    [HttpPost("/admin/emails/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("em.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var accounts = await db.EmailAccounts.Include(a => a.Channels)
            .Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        int ok = 0, skipped = ids.Length - accounts.Count;

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var account in accounts)
            {
                switch (act)
                {
                    case "enable" or "disable":
                        foreach (var channel in account.Channels)
                            channel.IsActive = act == "enable";
                        ok++;
                        break;
                    case "delete":
                        if (await IsAccountInUseAsync(account.Id, ct))
                        {
                            skipped++;
                        }
                        else
                        {
                            db.EmailAccounts.Remove(account); // channels cascade
                            ok++;
                        }
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["EmailsToastOk"] = ok;
        TempData["EmailsToastSkipped"] = skipped;
        TempData["EmailsToast"] = skipped > 0 ? "em.bulkPartial" : "em.bulkDone";
        if (skipped > 0)
            TempData["EmailsToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- email-edit.html ------------------------------------------------------------------

    /// <summary>Editor page; without ?id= it is the "＋ Yeni E-posta" create form
    /// (the mockup's new-button links straight to email-edit.html).</summary>
    [HttpGet("/admin/email-edit")]
    [NavKey("emails")]
    public async Task<IActionResult> Edit(int? id, CancellationToken ct = default)
    {
        EmailAccount? account = null;
        if (id is not null)
        {
            account = await db.EmailAccounts.Include(a => a.Channels)
                .SingleOrDefaultAsync(a => a.Id == id, ct);
            if (account is null)
                return NotFound();
        }

        return View(new EmailEditVm(
            account,
            account?.Channels.FirstOrDefault(c => c.Kind == EmailChannelKind.Mailbox),
            account?.Channels.FirstOrDefault(c => c.Kind == EmailChannelKind.Smtp),
            await db.Departments.OrderBy(d => d.Path)
                .Select(d => new OptionVm(d.Id, d.Name)).ToListAsync(ct),
            // Mockup order Düşük → Kritik = descending urgency value (lower = more urgent).
            await db.TicketPriorities.OrderByDescending(p => p.Urgency)
                .Select(p => new StatusOptionVm(p.Id, p.Key, p.Name)).ToListAsync(ct),
            await TopicOptionsAsync(ct)));
    }

    /// <summary>
    /// Whole-page save (B3): identity + new-ticket routing + both transport channels
    /// (always materialized as the account's one Mailbox and one Smtp row) + the
    /// dialog credentials in one post. Secrets follow the write-only contract:
    /// an empty or bullets-only value keeps the stored secret.
    /// </summary>
    [HttpPost("/admin/email-edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        int? id, string? address, string? fromName,
        int? deptId, int? priorityId, int? topicId, bool noAutoResp, string? notes,
        string? inHost, int? inPort, string? inFolder, string? inProtocol, string? inAuth,
        bool fetchActive, int? freq, int? maxFetch, string? afterFetch, string? archiveFolder,
        string? inUsername, string? inPassword, string? inClientId, string? inClientSecret,
        bool smtpActive, string? outHost, int? outPort, string? outAuth,
        string? outUsername, string? outPassword, string? outClientId, string? outClientSecret,
        bool spoofing, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        address = (address ?? "").Trim();
        if (!IsValidAddress(address))
            return EditToastBack(id, "ee.errEmail");
        if (await db.EmailAccounts.AnyAsync(a => a.Address == address && a.Id != id, ct))
            return EditToastBack(id, "ee.errEmailInUse");

        // B3 server side: an ENABLED channel needs a reachable endpoint definition.
        inHost = (inHost ?? "").Trim();
        outHost = (outHost ?? "").Trim();
        if (fetchActive && (inHost.Length == 0 || inPort is null or < 1 or > 65535))
            return EditToastBack(id, "ee.errIncoming");
        if (smtpActive && (outHost.Length == 0 || outPort is null or < 1 or > 65535))
            return EditToastBack(id, "ee.errOutgoing");
        if (inPort is < 0 or > 65535 || outPort is < 0 or > 65535)
            return EditToastBack(id, "ee.errPort");

        // Soft-ref selects: silently drop ids that no longer exist (helptopics precedent).
        if (deptId is { } dep && !await db.Departments.AnyAsync(d => d.Id == dep, ct)) deptId = null;
        if (priorityId is { } pr && !await db.TicketPriorities.AnyAsync(p => p.Id == pr, ct)) priorityId = null;
        if (topicId is { } tp && !await db.HelpTopics.AnyAsync(t => t.Id == tp, ct)) topicId = null;

        var isCreate = id is null;
        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            EmailAccount account;
            if (id is null)
            {
                account = new EmailAccount { Address = address };
                db.EmailAccounts.Add(account);
            }
            else
            {
                var found = await db.EmailAccounts.Include(a => a.Channels)
                    .SingleOrDefaultAsync(a => a.Id == id, ct);
                if (found is null)
                    return NotFound();
                account = found;
            }

            account.Address = address;
            account.DisplayName = string.IsNullOrWhiteSpace(fromName) ? null : fromName.Trim();
            account.DepartmentId = deptId;
            account.PriorityId = priorityId;
            account.HelpTopicId = topicId;
            account.NoAutoResponse = noAutoResp;
            account.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

            // ---- Incoming Mailbox channel ----------------------------------------------
            var mailbox = Channel(account, EmailChannelKind.Mailbox);
            mailbox.IsActive = fetchActive;
            mailbox.Protocol = inProtocol == "pop" ? MailProtocol.Pop : MailProtocol.Imap;
            mailbox.AuthKind = inAuth == "basic" ? MailAuthKind.Basic : MailAuthKind.OAuth2;
            mailbox.Host = inHost;
            mailbox.Port = inPort ?? 0;
            mailbox.Folder = string.IsNullOrWhiteSpace(inFolder) ? null : inFolder.Trim();
            mailbox.FetchFrequencyMinutes = Math.Clamp(freq ?? 5, 1, 1440);
            mailbox.FetchMax = Math.Clamp(maxFetch ?? 30, 1, 1000);
            mailbox.PostFetch = afterFetch switch
            {
                "delete" => PostFetchAction.Delete,
                "leave" => PostFetchAction.Nothing,
                _ => PostFetchAction.Archive,
            };
            mailbox.ArchiveFolder = string.IsNullOrWhiteSpace(archiveFolder) ? null : archiveFolder.Trim();
            ApplyCredentials(mailbox, inUsername, inPassword, inClientId, inClientSecret);

            // ---- Outgoing Smtp channel -------------------------------------------------
            var smtp = Channel(account, EmailChannelKind.Smtp);
            smtp.IsActive = smtpActive;
            smtp.Protocol = MailProtocol.Smtp;
            smtp.AuthKind = outAuth == "basic" ? MailAuthKind.Basic : MailAuthKind.OAuth2;
            smtp.Host = outHost;
            smtp.Port = outPort ?? 0;
            smtp.AllowSpoofing = spoofing;
            ApplyCredentials(smtp, outUsername, outPassword, outClientId, outClientSecret);

            await db.SaveChangesAsync(ct);
            id = account.Id;
        }

        TempData["EmailEditToast"] = isCreate ? "ee.toastCreated" : "ee.toastSaved";
        return Redirect($"/admin/email-edit?id={id}");
    }

    /// <summary>
    /// REAL "Bağlantıyı Sına" (ROADMAP row; invented UI — the mockup defines no
    /// control): a live MailKit probe of the POSTED, unsaved settings with a short
    /// timeout, returning a typed stage (input/dns/connect/tls/auth/ok/ok-noauth).
    /// A sentinel password falls back to the stored (decrypted) secret when ?id= is
    /// a saved account.
    /// </summary>
    [HttpPost("/admin/email-edit/test")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Test(
        int? id, string? kind, string? protocol, string? host, int? port,
        string? auth, string? username, string? password, CancellationToken ct)
    {
        var isSmtp = kind == "out";
        var channelKind = isSmtp ? EmailChannelKind.Smtp : EmailChannelKind.Mailbox;

        if (SecretUnchanged(password) && id is not null)
        {
            var stored = await db.EmailAccounts.Where(a => a.Id == id)
                .SelectMany(a => a.Channels)
                .Where(c => c.Kind == channelKind)
                .Select(c => c.PasswordProtected)
                .FirstOrDefaultAsync(ct);
            password = secrets.Unprotect(stored);
        }
        else if (SecretUnchanged(password))
        {
            password = null;
        }

        var result = await tester.TestAsync(new MailTestRequest(
            channelKind,
            isSmtp ? MailProtocol.Smtp : protocol == "pop" ? MailProtocol.Pop : MailProtocol.Imap,
            (host ?? "").Trim(),
            port ?? 0,
            auth == "basic" ? MailAuthKind.Basic : MailAuthKind.OAuth2,
            string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            password), ct);

        return Json(new { ok = result.Success, stage = result.Stage });
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>The account's single channel of a kind, materialized on first save
    /// (unique (account, kind) index — one Mailbox + one Smtp per address).</summary>
    private EmailChannel Channel(EmailAccount account, EmailChannelKind kind)
    {
        var channel = account.Channels.FirstOrDefault(c => c.Kind == kind);
        if (channel is null)
        {
            channel = new EmailChannel { Kind = kind };
            account.Channels.Add(channel);
        }
        return channel;
    }

    /// <summary>Write-only secret contract: empty / bullets-only posted values keep
    /// the stored ciphertext (there is no explicit "clear" — switch the auth mode
    /// instead; flagged).</summary>
    private void ApplyCredentials(EmailChannel channel, string? username, string? password, string? clientId, string? clientSecret)
    {
        channel.Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        channel.OAuthClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();
        if (!SecretUnchanged(password))
            channel.PasswordProtected = secrets.Protect(password!);
        if (!SecretUnchanged(clientSecret))
            channel.OAuthClientSecretProtected = secrets.Protect(clientSecret!);
    }

    private static bool SecretUnchanged(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().All(c => c == '•');

    private static bool IsValidAddress(string address) =>
        address.Length is > 2 and <= 120
        && System.Net.Mail.MailAddress.TryCreate(address, out var parsed)
        && parsed.Address == address;

    /// <summary>Referenced accounts are undeletable: departments (outgoing +
    /// auto-response address), filters (channel restriction) and the email settings
    /// (default system / alert / default-SMTP address) point here by id.</summary>
    private async Task<bool> IsAccountInUseAsync(int accountId, CancellationToken ct)
    {
        if (await db.Departments.AnyAsync(d => d.EmailAccountId == accountId || d.AutoResponseEmailAccountId == accountId, ct))
            return true;
        if (await db.Filters.AnyAsync(f => f.EmailAccountId == accountId, ct))
            return true;
        var email = await settings.GetEmailAsync(ct);
        return email.DefaultEmailAccountId == accountId
            || email.AlertEmailAccountId == accountId
            || (int.TryParse(email.DefaultSmtp, out var smtpId) && smtpId == accountId);
    }

    /// <summary>Help-topic select options: full "Parent / Child" labels in tree order
    /// (helptopics precedent).</summary>
    private async Task<List<OptionVm>> TopicOptionsAsync(CancellationToken ct)
    {
        var all = await db.HelpTopics
            .Select(t => new { t.Id, t.Name, t.ParentId, t.Sort })
            .ToListAsync(ct);
        var names = all.ToDictionary(t => t.Id, t => (t.Name, t.ParentId));
        string FullName(int tid)
        {
            var parts = new List<string>();
            int? probe = tid;
            var hops = 0;
            while (probe is { } pid && names.TryGetValue(pid, out var entry) && hops++ < 100)
            {
                parts.Insert(0, entry.Name);
                probe = entry.ParentId;
            }
            return string.Join(" / ", parts);
        }
        return [.. all.OrderBy(t => t.Sort).Select(t => new OptionVm(t.Id, FullName(t.Id)))];
    }

    private IActionResult EditToastBack(int? id, string key)
    {
        TempData["EmailEditToast"] = key;
        TempData["EmailEditToastError"] = true;
        return Redirect(id is null ? "/admin/email-edit" : $"/admin/email-edit?id={id}");
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["EmailsToast"] = key;
        if (error)
            TempData["EmailsToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public sealed record EmailsIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<EmailRowVm> Rows);

public sealed record EmailRowVm(
    int Id,
    string Address,
    string? DisplayName,
    string? PriorityKey,
    string? DeptName,
    DateTimeOffset Updated);

public sealed record EmailEditVm(
    EmailAccount? Account,
    EmailChannel? Mailbox,
    EmailChannel? Smtp,
    IReadOnlyList<OptionVm> Departments,
    IReadOnlyList<StatusOptionVm> Priorities,
    IReadOnlyList<OptionVm> Topics);
