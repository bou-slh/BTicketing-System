using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Auditing;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>Typed outcome of one processed inbound message (mirrors the persisted
/// <see cref="EmailInbound"/> row the processor writes). <c>Duplicate</c> = the
/// message was already bookkept — no new row was written.</summary>
public sealed record InboundProcessResult(
    EmailInboundStatus Status,
    string? Reason = null,
    int? TicketId = null,
    int? ThreadEntryId = null,
    bool Duplicate = false);

/// <summary>
/// S8 inbound pipeline: MimeMessage → ticket/thread (osTicket class.mailfetch /
/// class.mailparse / Ticket::create-from-email modeled, never copied). Order of
/// protection gates (osTicket parity): duplicate bookkeeping → self-mail → loop tag
/// (<see cref="MailPipelineHeaders.LoopTag"/> ≥ <see cref="MailPipelineHeaders.MaxPasses"/>)
/// → bounce/DSN (flags the correlated outbound row Failed, never creates tickets)
/// → banlist + filter Reject (FilterEngine.RunAsync — the Email-target filters built
/// in S7 go LIVE here) → reply threading (signed Message-Id token, outbox Message-Id
/// lookup, subject [#number] last resort with the sender-must-participate guard)
/// → append or create. Every message ends as one persisted EmailInbound row; drops
/// and rejects additionally hit the syslog (admin/system-logs).
///
/// Auto-submitted mail (Auto-Submitted != no, X-Auto-Response-Suppress AutoReply,
/// Precedence bulk/auto_reply/junk/list, X-Autoreply…) is deliberately NOT a gate: it
/// creates or appends like any other message, but with the whole notify cascade muted
/// and — on an existing ticket — no reopen (osTicket <c>$autorespond = $reopen =
/// false</c> in postMessage/Ticket::create). Slice 4 dropped these outright; that lost
/// real customer content sent from mailing-list and helpdesk-to-helpdesk setups, and
/// is realigned here. Genuine loops stay hard drops: our own <c>X-RapidsolDestek-Mail</c>
/// tag past <see cref="MailPipelineHeaders.MaxPasses"/>, self-addressed mail, and DSNs.
/// </summary>
public sealed partial class InboundMailProcessor(
    AppDbContext db,
    ISettingsService settings,
    IFilterEngine filters,
    ITicketService tickets,
    IThreadService threads,
    IUserService users,
    IFileStore files,
    IMailThreadTokenService token,
    ISystemLogService syslog,
    ILogger<InboundMailProcessor> logger)
{
    /// <summary>osTicket subject last-resort pattern (simplified): "[#R716555]" or
    /// "#R716555" — the number token after '#'.</summary>
    [GeneratedRegex(@"#([A-Za-z0-9][A-Za-z0-9._-]*)")]
    private static partial Regex SubjectNumber();

    public async Task<InboundProcessResult> ProcessAsync(
        EmailAccount account, EmailChannel channel, string uid, MimeMessage message,
        CancellationToken ct = default)
    {
        var mid = Normalize(message.MessageId);
        var sender = message.From.Mailboxes.FirstOrDefault() ?? message.Sender;
        var senderAddress = sender?.Address?.Trim();
        var subject = (message.Subject ?? "").Trim();

        // ---- idempotency (per-channel UID + per-account Message-Id backstop) ----------
        if (await db.EmailInbounds.AnyAsync(i => i.EmailChannelId == channel.Id && i.Uid == uid, ct)
            || (mid is not null && await db.EmailInbounds
                .AnyAsync(i => i.EmailAccountId == account.Id && i.MessageId == mid, ct)))
        {
            return new InboundProcessResult(EmailInboundStatus.Skipped, "duplicate", Duplicate: true);
        }

        Task<InboundProcessResult> SkipAsync(string reason, SystemLogType level = SystemLogType.Warning) =>
            RecordAsync(account, channel, uid, mid, senderAddress, subject,
                new InboundProcessResult(EmailInboundStatus.Skipped, reason), level, ct);

        if (string.IsNullOrEmpty(senderAddress))
            return await SkipAsync("no-sender");

        // ---- protection gates, in osTicket order --------------------------------------
        if (await db.EmailAccounts.AnyAsync(a => a.Address.ToLower() == senderAddress.ToLower(), ct))
            return await SkipAsync("self-mail"); // one of our own addresses — mail loop

        if (message.Headers.Count(h => string.Equals(h.Field, MailPipelineHeaders.LoopTag,
                StringComparison.OrdinalIgnoreCase)) >= MailPipelineHeaders.MaxPasses)
        {
            return await SkipAsync("loop");
        }

        if (IsDeliveryStatusReport(message, out var failed, out var originalMid))
        {
            if (failed && originalMid is not null)
                await FlagBouncedOutboundAsync(originalMid, ct);
            return await SkipAsync(failed ? "bounce" : "delivery-report");
        }

        // Auto-submitted mail is NOT dropped (osTicket postMessage / Ticket::create):
        // it threads or opens a ticket like any other message, with the autoresponse
        // and — on an existing ticket — the reopen muted, so an out-of-office bounce
        // neither answers itself nor resurrects a closed ticket.
        var autoSubmitted = IsAutoSubmitted(message);

        // ---- ban gate + filter reject (S7 Email-target filters go LIVE here) ----------
        var existingUser = await db.Users
            .Where(u => u.Emails.Any(e => e.Address.ToLower() == senderAddress.ToLower()))
            .Select(u => new { u.Id, u.Name, Org = u.Organization != null ? u.Organization.Name : null })
            .FirstOrDefaultAsync(ct);
        var email = await settings.GetEmailAsync(ct);
        var topicId = await ResolveTopicIdAsync(account, ct);
        var (body, rawText) = ExtractBody(message, email.StripQuoted, email.ReplySeparator);
        var replyTo = message.ReplyTo.Mailboxes.FirstOrDefault()?.Address;
        var outcome = await filters.RunAsync(new FilterInput
        {
            Source = TicketSource.Email,
            EmailAccountId = account.Id,
            Name = existingUser?.Name ?? DisplayName(sender!, senderAddress),
            Email = senderAddress,
            ReplyTo = replyTo,
            Subject = subject,
            Body = rawText,
            TopicName = topicId is { } t
                ? await db.HelpTopics.Where(h => h.Id == t).Select(h => h.Name).FirstOrDefaultAsync(ct)
                : null,
            OrgName = existingUser?.Org,
        }, ct);
        if (outcome.RejectedBy is { } rejecting)
        {
            var reason = rejecting == FilterEngine.BanlistName ? "banlist" : $"filter:{rejecting}";
            return await RecordAsync(account, channel, uid, mid, senderAddress, subject,
                new InboundProcessResult(EmailInboundStatus.Rejected, reason), SystemLogType.Warning, ct);
        }

        // ---- reply threading ----------------------------------------------------------
        var matched = await MatchTicketAsync(message, senderAddress, ct);
        var result = matched is { } ticketId
            ? await AppendAsync(ticketId, account, sender!, senderAddress, subject, body, message, email,
                autoSubmitted, ct)
            : await CreateAsync(account, sender!, senderAddress, subject, body, replyTo, topicId, message, email,
                autoSubmitted, ct);

        return await RecordAsync(account, channel, uid, mid, senderAddress, subject, result,
            result.Status is EmailInboundStatus.TicketCreated or EmailInboundStatus.ThreadAppended
                ? SystemLogType.Debug
                : SystemLogType.Warning, ct);
    }

    // ---- new ticket ---------------------------------------------------------------------

    private async Task<InboundProcessResult> CreateAsync(
        EmailAccount account, MailboxAddress sender, string senderAddress, string subject,
        string body, string? replyTo, int? topicId, MimeMessage message, EmailSettings email,
        bool autoSubmitted, CancellationToken ct)
    {
        var (user, userEmailId) = await FindOrCreateUserAsync(
            DisplayName(sender, senderAddress), senderAddress, email.AcceptUnregistered, ct);
        if (user is null)
            return new InboundProcessResult(EmailInboundStatus.Skipped, "unregistered");

        Ticket ticket;
        try
        {
            ticket = await tickets.CreateAsync(new TicketCreateRequest
            {
                UserId = user.Id,
                UserEmailId = userEmailId,
                Subject = subject.Length > 0 ? subject : "(Konu yok)",
                Body = body,
                HelpTopicId = topicId,
                DepartmentId = account.DepartmentId,
                PriorityId = await EmailPriorityIdAsync(message, email, ct) ?? account.PriorityId,
                EmailAccountId = account.Id,
                Source = TicketSource.Email,
                ReplyTo = replyTo,
                // osTicket email noautoresp: the account mutes its autoresponses.
                // An auto-submitted first mail joins it (osTicket Ticket::create:
                // "$autorespond = false if $message->isAutoReply()") — answering a
                // robot is how mail loops start.
                DisableAutoResponse = account.NoAutoResponse || autoSubmitted,
            }, ActorContext.System, ct);
        }
        catch (TicketRejectedByFilterException ex)
        {
            // The engine ran twice (pre-gate + inside CreateAsync) — deterministic,
            // so only a filter matching topic-swapped values can first reject here.
            return new InboundProcessResult(EmailInboundStatus.Rejected, $"filter:{ex.FilterName}");
        }

        var entryId = await db.ThreadEntries
            .Where(e => e.ThreadId == ticket.ThreadId && e.Type == ThreadEntryType.Message)
            .OrderBy(e => e.Id).Select(e => e.Id).FirstAsync(ct);
        await SaveAttachmentsAsync(message, entryId, ct);
        await AddCollaboratorsAsync(message, ticket.ThreadId, ticket.UserId, senderAddress, email, ct);

        return new InboundProcessResult(EmailInboundStatus.TicketCreated, null, ticket.Id, entryId);
    }

    // ---- reply on an existing ticket ----------------------------------------------------

    private async Task<InboundProcessResult> AppendAsync(
        int ticketId, EmailAccount account, MailboxAddress sender, string senderAddress,
        string subject, string body, MimeMessage message, EmailSettings email,
        bool autoSubmitted, CancellationToken ct)
    {
        var ticket = await db.Tickets.SingleAsync(t => t.Id == ticketId, ct);
        var participant = await db.Users
            .Where(u => u.Emails.Any(e => e.Address.ToLower() == senderAddress.ToLower()))
            .FirstOrDefaultAsync(ct);
        var isParticipant = participant is not null
            && (ticket.UserId == participant.Id || await db.ThreadCollaborators
                .AnyAsync(c => c.ThreadId == ticket.ThreadId && c.UserId == participant.Id, ct));
        var staff = participant is null
            ? await db.Staff.FirstOrDefaultAsync(s => s.IsActive && s.Email != null
                && s.Email.ToLower() == senderAddress.ToLower(), ct)
            : null;

        ActorContext actor;
        ThreadEntryType type;
        if (staff is not null)
        {
            // A staff sender mailing in on a ticket posts a public Response (osTicket
            // staff-reply-by-email semantics). The mail already left the sender's
            // outbox, so the effort work gate cannot hold it — bypassed knowingly.
            actor = ActorContext.ForStaff(staff);
            type = ThreadEntryType.Response;
        }
        else
        {
            User? user = participant;
            if (user is null)
            {
                (user, _) = await FindOrCreateUserAsync(
                    DisplayName(sender, senderAddress), senderAddress, email.AcceptUnregistered, ct);
                if (user is null)
                    return new InboundProcessResult(EmailInboundStatus.Skipped, "unregistered");
            }
            if (!isParticipant && user.Id != ticket.UserId && email.AutoAddCollabs)
                await threads.AddCollaboratorAsync(ticket.ThreadId, user.Id, CollaboratorRole.Cc, ActorContext.System, ct);
            actor = ActorContext.ForUser(user);
            type = ThreadEntryType.Message;
        }

        // Closed-ticket reply → reopen (osTicket Ticket::onMessage): only when the
        // current status allows it; target = the status's configured reopen status,
        // else the default open status. System actor — the reopen is the pipeline's,
        // not the sender's (no staff permission check applies). An auto-submitted mail
        // never reopens: osTicket's $reopen rides the same flag as $autorespond
        // precisely so a vacation responder cannot resurrect a closed ticket.
        var status = await db.TicketStatuses.SingleAsync(s => s.Id == ticket.StatusId, ct);
        if (!autoSubmitted && status.State != TicketState.Open && status.AllowReopen)
        {
            var reopenTo = status.ReopenStatusId
                ?? await db.TicketStatuses.Where(s => s.Key == "open").Select(s => s.Id).SingleAsync(ct);
            await tickets.TransitionStatusAsync(ticket.Id, reopenTo, ActorContext.System, ct);
        }

        var entry = await threads.PostAsync(ticket.ThreadId, type, body, actor,
            new PostOptions
            {
                Source = "Email",
                Title = subject.Length > 0 ? subject : null,
                BypassWorkGate = true,
                SuppressNotifications = autoSubmitted,
                // Files must exist before the mail handlers read the entry (a staff
                // reply-by-email is forwarded to the customer with its attachments).
                OnPosted = (posted, token) => SaveAttachmentsAsync(message, posted.Id, token),
            }, ct);
        await AddCollaboratorsAsync(message, ticket.ThreadId, ticket.UserId, senderAddress, email, ct);

        return new InboundProcessResult(EmailInboundStatus.ThreadAppended, null, ticket.Id, entry.Id);
    }

    // ---- threading match ----------------------------------------------------------------

    /// <summary>Signed token in In-Reply-To/References → outbox Message-Id lookup →
    /// subject "[#number]" last resort (sender must be owner/collaborator —
    /// osTicket's anti-injection guard).</summary>
    private async Task<int?> MatchTicketAsync(MimeMessage message, string senderAddress, CancellationToken ct)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
            candidates.Add(message.InReplyTo);
        candidates.AddRange(message.References);

        foreach (var candidate in candidates.Select(Normalize).OfType<string>().Distinct())
        {
            if (await token.TryDecodeTicketIdAsync(candidate, ct) is { } fromToken
                && await db.Tickets.AnyAsync(t => t.Id == fromToken, ct))
            {
                return fromToken;
            }
            var fromOutbox = await db.EmailOutbounds
                .Where(o => o.MessageId == candidate && o.TicketId != null)
                .Select(o => o.TicketId).FirstOrDefaultAsync(ct);
            if (fromOutbox is { } outboxTicket)
                return outboxTicket;
        }

        var match = SubjectNumber().Match(message.Subject ?? "");
        if (match.Success)
        {
            var number = match.Groups[1].Value.TrimEnd('.', '_', '-');
            var ticket = await db.Tickets
                .Where(t => t.Number == number)
                .Select(t => new { t.Id, t.UserId, t.ThreadId })
                .FirstOrDefaultAsync(ct);
            if (ticket is not null)
            {
                var senderIsParticipant = await db.UserEmails
                    .Where(e => e.Address.ToLower() == senderAddress.ToLower())
                    .AnyAsync(e => e.UserId == ticket.UserId || db.ThreadCollaborators
                        .Any(c => c.ThreadId == ticket.ThreadId && c.UserId == e.UserId), ct);
                if (senderIsParticipant)
                    return ticket.Id;
            }
        }
        return null;
    }

    // ---- pure-ish helpers ---------------------------------------------------------------

    /// <summary>Auto-submitted detection (osTicket TicketFilter::isAutoReply subset):
    /// Auto-Submitted anything but "no", X-Auto-Response-Suppress mentioning
    /// AutoReply, Precedence/X-Precedence bulk|auto_reply|junk|list, and the common
    /// X-Autoreply/X-Autorespond markers.</summary>
    public static bool IsAutoSubmitted(MimeMessage message)
    {
        foreach (var header in message.Headers)
        {
            var value = (header.Value ?? "").Trim();
            switch (header.Field.ToLowerInvariant())
            {
                case "auto-submitted" when value.Length > 0
                    && !value.StartsWith("no", StringComparison.OrdinalIgnoreCase):
                case "x-auto-response-suppress" when value.Contains("AutoReply", StringComparison.OrdinalIgnoreCase):
                case "precedence" or "x-precedence" when
                    value.StartsWith("bulk", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("auto_reply", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("junk", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("list", StringComparison.OrdinalIgnoreCase):
                case "x-autoreply" when value.StartsWith("yes", StringComparison.OrdinalIgnoreCase):
                case "x-autorespond" or "x-autoresponse":
                    return true;
            }
        }
        return false;
    }

    /// <summary>DSN detection (osTicket Mail_Parse::isBounceNotice): multipart/report
    /// with report-type delivery-status, or any message/delivery-status part.
    /// <paramref name="failed"/> = a per-recipient Action field says "failed";
    /// <paramref name="originalMid"/> = the bounced original's Message-Id (from the
    /// embedded message/rfc822 or text/rfc822-headers part) for outbox correlation.</summary>
    public static bool IsDeliveryStatusReport(MimeMessage message, out bool failed, out string? originalMid)
    {
        failed = false;
        originalMid = null;

        var isReport = message.Body is MultipartReport report
            && string.Equals(report.ReportType, "delivery-status", StringComparison.OrdinalIgnoreCase);
        foreach (var part in message.BodyParts.OfType<MessageDeliveryStatus>())
        {
            isReport = true;
            foreach (var group in part.StatusGroups)
            {
                var action = group["Action"];
                if (action is not null && action.Trim().Equals("failed", StringComparison.OrdinalIgnoreCase))
                    failed = true;
            }
        }
        if (!isReport)
            return false;

        originalMid = Normalize(message.BodyParts.OfType<MessagePart>()
            .Select(p => p.Message?.MessageId).FirstOrDefault(m => m is not null));
        return true;
    }

    /// <summary>Body of the mail as sanitizer-ready HTML (html preferred, text
    /// encoded with line breaks kept) plus the plain text the filters match on.</summary>
    public static (string Html, string Text) ExtractBody(MimeMessage message, bool stripQuoted, string? separator)
    {
        var html = message.HtmlBody;
        var text = message.TextBody ?? "";
        if (!string.IsNullOrWhiteSpace(html))
        {
            var stripped = stripQuoted ? QuotedReplyStripper.StripHtml(html, separator) : html;
            return (stripped, text.Length > 0 ? text : Regex.Replace(stripped, "<[^>]*>", " ").Trim());
        }

        if (stripQuoted)
            text = QuotedReplyStripper.StripText(text, separator);
        var encoded = string.Join("<br>",
            text.Replace("\r\n", "\n").Split('\n').Select(WebUtility.HtmlEncode));
        return (encoded.Length > 0 ? encoded : "-", text);
    }

    private static string DisplayName(MailboxAddress sender, string address)
    {
        if (!string.IsNullOrWhiteSpace(sender.Name))
            return sender.Name.Trim();
        var at = address.IndexOf('@');
        return at > 0 ? address[..at] : address;
    }

    private static string? Normalize(string? messageId)
    {
        var mid = messageId?.Trim().Trim('<', '>');
        return string.IsNullOrEmpty(mid) ? null : mid;
    }

    // ---- side effects -------------------------------------------------------------------

    /// <summary>email/use_email_priority: X-Priority/Importance headers map onto the
    /// high/low priority rows (osTicket getPriority over mail headers).</summary>
    private async Task<int?> EmailPriorityIdAsync(MimeMessage message, EmailSettings email, CancellationToken ct)
    {
        if (!email.UseEmailPriority)
            return null;
        string? key = null;
        if (message.Priority == MessagePriority.Urgent || message.Importance == MessageImportance.High
            || message.XPriority is XMessagePriority.Highest or XMessagePriority.High)
        {
            key = "high";
        }
        else if (message.Priority == MessagePriority.NonUrgent || message.Importance == MessageImportance.Low
            || message.XPriority is XMessagePriority.Lowest or XMessagePriority.Low)
        {
            key = "low";
        }
        if (key is null)
            return null;
        return await db.TicketPriorities.Where(p => p.Key == key).Select(p => (int?)p.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>Sender → (user, matching email id). Unknown senders are created
    /// (org auto-link via UserService) unless email/accept_unregistered is off.</summary>
    private async Task<(User? User, int? UserEmailId)> FindOrCreateUserAsync(
        string name, string address, bool acceptUnregistered, CancellationToken ct)
    {
        var existing = await db.UserEmails.Include(e => e.User)
            .FirstOrDefaultAsync(e => e.Address.ToLower() == address.ToLower(), ct);
        if (existing is not null)
            return (existing.User, existing.Id);
        if (!acceptUnregistered)
            return (null, null);

        try
        {
            var user = await users.CreateAsync(new UserCreateRequest { Name = name, Email = address },
                ActorContext.System, ct);
            return (user, user.Emails.FirstOrDefault()?.Id);
        }
        catch (DomainRuleException ex)
        {
            logger.LogWarning("Inbound sender {Address} could not be registered: {Code}", address, ex.Code);
            return (null, null);
        }
    }

    /// <summary>To/Cc third parties → find-or-create + ThreadCollaborator (osTicket
    /// auto_add_collabs — mapped 1:1 onto email/auto_add_collabs).</summary>
    private async Task AddCollaboratorsAsync(MimeMessage message, int threadId, int ownerUserId,
        string senderAddress, EmailSettings email, CancellationToken ct)
    {
        if (!email.AutoAddCollabs)
            return;
        var ourAddresses = new HashSet<string>(
            await db.EmailAccounts.Select(a => a.Address.ToLower()).ToListAsync(ct));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { senderAddress };
        foreach (var mailbox in message.To.Mailboxes.Concat(message.Cc.Mailboxes))
        {
            var address = mailbox.Address?.Trim();
            if (string.IsNullOrEmpty(address) || !seen.Add(address)
                || ourAddresses.Contains(address.ToLowerInvariant()))
            {
                continue;
            }
            var (user, _) = await FindOrCreateUserAsync(
                DisplayName(mailbox, address), address, email.AcceptUnregistered, ct);
            if (user is null || user.Id == ownerUserId)
                continue;
            await threads.AddCollaboratorAsync(threadId, user.Id, CollaboratorRole.Cc, ActorContext.System, ct);
        }
    }

    /// <summary>MimeMessage attachments → IFileStore + StoredFile/Attachment rows on
    /// the entry (portal OpenController pattern — mail is the 6th upload ingress).
    /// attachments/max_size_mb is honored per file: oversized ones are skipped with a
    /// syslog note, the message itself is kept (osTicket parity).</summary>
    private async Task SaveAttachmentsAsync(MimeMessage message, int entryId, CancellationToken ct)
    {
        var caps = await settings.GetAttachmentsAsync(ct);
        var any = false;
        foreach (var part in message.Attachments.OfType<MimePart>())
        {
            if (part.Content is null)
                continue; // malformed part without a body — nothing to store
            var name = string.IsNullOrWhiteSpace(part.FileName) ? "ek" : part.FileName;
            using var buffer = new MemoryStream();
            await part.Content.DecodeToAsync(buffer, ct);
            if (buffer.Length > caps.MaxSizeBytes)
            {
                await syslog.LogAsync(SystemLogType.Warning,
                    $"E-posta eki atlandı: {name}",
                    $"Boyut {buffer.Length} bayt, izin verilen üst sınır {caps.MaxSizeMb} MB.",
                    logger: "mail", ct: ct);
                continue;
            }
            buffer.Position = 0;
            var stored = await files.SaveAsync(buffer, name, part.ContentType.MimeType, ct);
            db.StoredFiles.Add(stored);
            db.Attachments.Add(new Attachment
            {
                ObjectType = AttachmentObjectType.ThreadEntry,
                ObjectId = entryId,
                File = stored,
                Inline = part.ContentDisposition?.Disposition?.Equals(
                    ContentDisposition.Inline, StringComparison.OrdinalIgnoreCase) == true,
            });
            any = true;
        }
        if (any)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>A DSN correlated back to our outbox: the Sent row is flagged Failed
    /// honestly — delivery to the final mailbox did not happen.</summary>
    private async Task FlagBouncedOutboundAsync(string originalMid, CancellationToken ct)
    {
        var row = await db.EmailOutbounds.FirstOrDefaultAsync(o => o.MessageId == originalMid, ct);
        if (row is null)
            return;
        row.Status = EmailOutboundStatus.Failed;
        row.LastError = "bounce: sunucu teslimat hatası bildirdi";
        await db.SaveChangesAsync(ct);
    }

    private async Task<int?> ResolveTopicIdAsync(EmailAccount account, CancellationToken ct)
    {
        // Account override → core.default_topic_id → none (the create's department
        // cascade still routes). Stale ids are skipped, not fatal.
        if (account.HelpTopicId is { } own && await db.HelpTopics.AnyAsync(h => h.Id == own, ct))
            return own;
        var raw = await settings.GetAsync("core", "default_topic_id", ct);
        if (int.TryParse(raw, out var configured) && configured > 0
            && await db.HelpTopics.AnyAsync(h => h.Id == configured, ct))
        {
            return configured;
        }
        return null;
    }

    /// <summary>Persists the EmailInbound row + syslog trail and returns the result.</summary>
    private async Task<InboundProcessResult> RecordAsync(
        EmailAccount account, EmailChannel channel, string uid, string? mid, string? from,
        string? subject, InboundProcessResult result, SystemLogType level, CancellationToken ct)
    {
        db.EmailInbounds.Add(new EmailInbound
        {
            EmailChannelId = channel.Id,
            EmailAccountId = account.Id,
            Uid = uid,
            MessageId = mid,
            FromAddress = from,
            Subject = subject,
            Status = result.Status,
            Reason = result.Reason,
            TicketId = result.TicketId,
            ThreadEntryId = result.ThreadEntryId,
        });
        await db.SaveChangesAsync(ct);

        if (result.Status is EmailInboundStatus.Skipped or EmailInboundStatus.Rejected)
        {
            await syslog.LogAsync(level,
                $"Gelen e-posta işlenmedi ({result.Reason})",
                $"Hesap: {account.Address}; Gönderen: {from ?? "?"}; Konu: {subject ?? ""}",
                logger: "mail", ct: ct);
        }
        logger.LogDebug("Inbound mail {Uid} on {Address}: {Status} {Reason}",
            uid, account.Address, result.Status, result.Reason);
        return result;
    }
}
