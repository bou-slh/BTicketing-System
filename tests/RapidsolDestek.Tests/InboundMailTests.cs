using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S8 slice 4: the inbound mail pipeline driven at the processor level with
/// constructed <see cref="MimeMessage"/>s (no IMAP server), plus MailFetchJob
/// orchestration over a scripted <see cref="FakeInboundMailClient"/>. Every test
/// fetches onto the seeded destek@ account's mailbox channel and asserts the
/// persisted EmailInbound bookkeeping alongside the domain effect.
/// </summary>
[Collection("Postgres")]
public class InboundMailTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2020, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---- fake mailbox ------------------------------------------------------------------

    /// <summary>Scripted IInboundMailClient: messages queued per test, post-fetch
    /// actions recorded so the job's honesty is observable.</summary>
    private sealed class FakeInboundMailClient : IInboundMailClient
    {
        public List<(string Uid, MimeMessage Message)> Messages { get; } = [];
        public List<(string Uid, PostFetchAction Action, string? Folder)> PostFetched { get; } = [];
        public InboundConnection? LastConnection { get; private set; }
        public Exception? ConnectFailure { get; set; }
        public int Connects { get; private set; }

        public Task<IInboundMailSession> ConnectAsync(InboundConnection connection, CancellationToken ct = default)
        {
            Connects++;
            LastConnection = connection;
            if (ConnectFailure is not null)
                throw ConnectFailure;
            return Task.FromResult<IInboundMailSession>(new Session(this));
        }

        private sealed class Session(FakeInboundMailClient owner) : IInboundMailSession
        {
            public Task<IReadOnlyList<string>> ListPendingUidsAsync(int max, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<string>>([.. owner.Messages.Take(max).Select(m => m.Uid)]);

            public Task<MimeMessage> DownloadAsync(string uid, CancellationToken ct = default) =>
                Task.FromResult(owner.Messages.Single(m => m.Uid == uid).Message);

            public Task ApplyPostFetchAsync(string uid, PostFetchAction action, string? archiveFolder,
                CancellationToken ct = default)
            {
                owner.PostFetched.Add((uid, action, archiveFolder));
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // ---- helpers -----------------------------------------------------------------------

    private static MimeMessage Mail(string from, string to, string subject, string text,
        string? html = null, Action<MimeMessage>? customize = null)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.MessageId = $"{Guid.NewGuid():N}@remote.example";
        message.Body = html is null
            ? new TextPart("plain") { Text = text }
            : new BodyBuilder { TextBody = text, HtmlBody = html }.ToMessageBody();
        customize?.Invoke(message);
        return message;
    }

    private async Task<(EmailAccount Account, EmailChannel Channel)> MailboxAsync(ServiceScopeBundle s)
    {
        var account = await s.Db.EmailAccounts.Include(a => a.Channels)
            .SingleAsync(a => a.Address == "destek@rapidsol.com.tr");
        return (account, account.Channels.Single(c => c.Kind == EmailChannelKind.Mailbox));
    }

    private async Task<InboundProcessResult> ProcessAsync(ServiceScopeBundle s, MimeMessage message,
        string? uid = null)
    {
        var (account, channel) = await MailboxAsync(s);
        return await s.Get<InboundMailProcessor>()
            .ProcessAsync(account, channel, uid ?? Guid.NewGuid().ToString("n")[..10], message);
    }

    /// <summary>Runs the fetch job with a fixed clock and the scripted client.</summary>
    private async Task RunFetchAsync(ServiceScopeBundle s, FakeInboundMailClient client, DateTimeOffset? now = null)
    {
        var job = new MailFetchJob(s.Db, s.Get<ISettingsService>(), client,
            s.Get<IMailCredentialResolver>(), s.Get<InboundMailProcessor>(),
            s.Get<ISystemLogService>(), new FixedTime(now ?? Now), NullLogger<MailFetchJob>.Instance);
        await job.RunAsync(CancellationToken.None);
    }

    /// <summary>Own ticket for the seeded canon user, so shared rows stay pristine.</summary>
    private static async Task<Ticket> OwnTicketAsync(ServiceScopeBundle s, string userName = "Bourla Salehi")
    {
        var owner = await TestActors.UserAsync(s.Db, userName);
        return await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Gelen posta testi {Guid.NewGuid():N}"[..28],
            Body = "<p>ilk mesaj</p>",
        }, owner);
    }

    private static async Task<string> UserAddressAsync(ServiceScopeBundle s, string name) =>
        await s.Db.Users.Where(u => u.Name == name)
            .SelectMany(u => u.Emails).Select(e => e.Address).FirstAsync();

    // ---- new ticket from mail ------------------------------------------------------------

    [Fact]
    public async Task NewTicket_FromMail_CreatesUser_RoutesTopic_QueuesAutoresponse_StoresAttachment()
    {
        using var s = new ServiceScopeBundle(fixture);
        var topicId = await s.Db.HelpTopics.Where(h => h.Name.Contains("Bordro"))
            .Select(h => (int?)h.Id).FirstOrDefaultAsync()
            ?? await s.Db.HelpTopics.Select(h => h.Id).FirstAsync();
        await using var topicOverride = await SettingOverride.SetAsync(
            fixture, "core", "default_topic_id", topicId.ToString());

        var address = $"yeni{Guid.NewGuid():N}"[..12] + "@disariden.example";
        fixture.Factory.Emails.Clear();

        using var s2 = new ServiceScopeBundle(fixture); // fresh settings memo
        var message = Mail(address, "destek@rapidsol.com.tr", "Yazıcı çalışmıyor",
            "Merhaba, ofisteki yazıcı açılmıyor.", customize: m =>
            {
                m.From.Clear();
                m.From.Add(new MailboxAddress("Deniz Yılmaz", address));
                var builder = new BodyBuilder { TextBody = "Merhaba, ofisteki yazıcı açılmıyor." };
                builder.Attachments.Add("rapor.txt",
                    System.Text.Encoding.UTF8.GetBytes("hata dökümü"), new ContentType("text", "plain"));
                m.Body = builder.ToMessageBody();
            });

        var result = await ProcessAsync(s2, message);

        Assert.Equal(EmailInboundStatus.TicketCreated, result.Status);
        var ticket = await s2.Db.Tickets.Include(t => t.User).SingleAsync(t => t.Id == result.TicketId);
        Assert.Equal(TicketSource.Email, ticket.Source);
        Assert.Equal(topicId, ticket.HelpTopicId);
        Assert.Equal("Yazıcı çalışmıyor", ticket.Subject);
        // Sender became a real user (name from the From display name).
        Assert.Equal("Deniz Yılmaz", ticket.User!.Name);
        Assert.NotNull(ticket.EmailAccountId);
        // The mail account the ticket arrived on is recorded.
        Assert.Equal((await MailboxAsync(s2)).Account.Id, ticket.EmailAccountId);

        // Attachment stored through IFileStore and hung off the initial message entry.
        var attachment = await s2.Db.Attachments.Include(a => a.File)
            .SingleAsync(a => a.ObjectId == result.ThreadEntryId
                && a.ObjectType == AttachmentObjectType.ThreadEntry);
        Assert.Equal("rapor.txt", attachment.File!.Name);

        // The existing S8 slice-2 fan-out mailed the new owner for FREE (no duplicate
        // send path in the inbound pipeline).
        Assert.Contains(fixture.Factory.Emails.Sent, m => m.To == address);

        // Bookkeeping row.
        var row = await s2.Db.EmailInbounds.SingleAsync(i => i.TicketId == ticket.Id);
        Assert.Equal(EmailInboundStatus.TicketCreated, row.Status);
        Assert.Equal(address, row.FromAddress);
    }

    [Fact]
    public async Task NewTicket_AttachmentOverCap_IsSkipped_MessageStillCreated()
    {
        await using var cap = await SettingOverride.SetAsync(fixture, "attachments", "max_size_mb", "1");
        using var s = new ServiceScopeBundle(fixture);

        var address = $"buyuk{Guid.NewGuid():N}"[..12] + "@disariden.example";
        var message = Mail(address, "destek@rapidsol.com.tr", "Büyük ek", "gövde", customize: m =>
        {
            var builder = new BodyBuilder { TextBody = "gövde" };
            builder.Attachments.Add("kocaman.bin", new byte[2 * 1024 * 1024],
                new ContentType("application", "octet-stream"));
            builder.Attachments.Add("kucuk.txt",
                System.Text.Encoding.UTF8.GetBytes("ufak"), new ContentType("text", "plain"));
            m.Body = builder.ToMessageBody();
        });

        var result = await ProcessAsync(s, message);

        Assert.Equal(EmailInboundStatus.TicketCreated, result.Status);
        var stored = await s.Db.Attachments.Include(a => a.File)
            .Where(a => a.ObjectId == result.ThreadEntryId
                && a.ObjectType == AttachmentObjectType.ThreadEntry)
            .Select(a => a.File!.Name).ToListAsync();
        Assert.Equal(["kucuk.txt"], stored); // the oversized one never reached the store
    }

    [Fact]
    public async Task NewTicket_UnregisteredSenderRefused_WhenAcceptUnregisteredOff()
    {
        await using var accept = await SettingOverride.SetAsync(fixture, "email", "accept_unregistered", "false");
        using var s = new ServiceScopeBundle(fixture);

        var address = $"davetsiz{Guid.NewGuid():N}"[..14] + "@disariden.example";
        var result = await ProcessAsync(s, Mail(address, "destek@rapidsol.com.tr", "Merhaba", "içerik"));

        Assert.Equal(EmailInboundStatus.Skipped, result.Status);
        Assert.Equal("unregistered", result.Reason);
        Assert.False(await s.Db.UserEmails.AnyAsync(e => e.Address == address));
    }

    // ---- threading ------------------------------------------------------------------------

    [Fact]
    public async Task Reply_WithSignedToken_AppendsToThread()
    {
        using var s = new ServiceScopeBundle(fixture);
        var ticket = await OwnTicketAsync(s);
        var owner = await UserAddressAsync(s, "Bourla Salehi");
        var messageId = await s.Get<IMailThreadTokenService>()
            .CreateMessageIdAsync(ticket.Id, "rapidsol.com.tr");

        var result = await ProcessAsync(s, Mail(owner, "destek@rapidsol.com.tr",
            "Re: konu", "Ek bilgi gönderiyorum.", customize: m => m.InReplyTo = messageId));

        Assert.Equal(EmailInboundStatus.ThreadAppended, result.Status);
        Assert.Equal(ticket.Id, result.TicketId);
        var entry = await s.Db.ThreadEntries.SingleAsync(e => e.Id == result.ThreadEntryId);
        Assert.Equal(ThreadEntryType.Message, entry.Type);
        Assert.Equal("Email", entry.Source);
        Assert.Contains("Ek bilgi", entry.Body);
    }

    [Fact]
    public async Task Reply_WithTamperedToken_DoesNotThread()
    {
        using var s = new ServiceScopeBundle(fixture);
        var ticket = await OwnTicketAsync(s);
        var owner = await UserAddressAsync(s, "Bourla Salehi");
        var real = await s.Get<IMailThreadTokenService>().CreateMessageIdAsync(ticket.Id, "rapidsol.com.tr");
        // Same shape, forged signature → must NOT thread (message-injection guard).
        var forged = string.Join('-', real.Split('-')[..3]) + "-000000000000@rapidsol.com.tr";

        var result = await ProcessAsync(s, Mail(owner, "destek@rapidsol.com.tr",
            "Konu başlığı", "başka içerik", customize: m => m.InReplyTo = forged));

        Assert.Equal(EmailInboundStatus.TicketCreated, result.Status);
        Assert.NotEqual(ticket.Id, result.TicketId);
    }

    [Fact]
    public async Task Reply_ViaOutboxMessageId_AppendsToThread()
    {
        using var s = new ServiceScopeBundle(fixture);
        var ticket = await OwnTicketAsync(s);
        var owner = await UserAddressAsync(s, "Bourla Salehi");
        // An outbox row whose Message-Id is NOT one of our signed tokens (e.g. a
        // pre-upgrade send) still threads through the stored-row lookup.
        var mid = $"legacy-{Guid.NewGuid():N}@rapidsol.com.tr";
        s.Db.EmailOutbounds.Add(new EmailOutbound
        {
            ToAddress = owner, Subject = "Bildirim", HtmlBody = "<p>x</p>",
            TicketId = ticket.Id, MessageId = mid, Status = EmailOutboundStatus.Sent,
        });
        await s.Db.SaveChangesAsync();

        var result = await ProcessAsync(s, Mail(owner, "destek@rapidsol.com.tr",
            "Cevap", "cevabım", customize: m => m.References.Add(mid)));

        Assert.Equal(EmailInboundStatus.ThreadAppended, result.Status);
        Assert.Equal(ticket.Id, result.TicketId);
    }

    [Fact]
    public async Task Reply_SubjectNumberFallback_MatchesOnlyForParticipants()
    {
        using var s = new ServiceScopeBundle(fixture);
        var ticket = await OwnTicketAsync(s);
        var owner = await UserAddressAsync(s, "Bourla Salehi");

        // Owner: subject [#number] threads (no headers at all).
        var owned = await ProcessAsync(s, Mail(owner, "destek@rapidsol.com.tr",
            $"[#{ticket.Number}] tekrar", "aynı konu"));
        Assert.Equal(EmailInboundStatus.ThreadAppended, owned.Status);
        Assert.Equal(ticket.Id, owned.TicketId);

        // A stranger quoting the same number must NOT get in (message injection).
        var stranger = $"yabanci{Guid.NewGuid():N}"[..14] + "@disariden.example";
        var injected = await ProcessAsync(s, Mail(stranger, "destek@rapidsol.com.tr",
            $"[#{ticket.Number}] araya girme", "izinsiz"));
        Assert.Equal(EmailInboundStatus.TicketCreated, injected.Status);
        Assert.NotEqual(ticket.Id, injected.TicketId);
    }

    [Fact]
    public async Task Reply_OnClosedTicket_Reopens()
    {
        using var s = new ServiceScopeBundle(fixture);
        var ticket = await OwnTicketAsync(s);
        var owner = await UserAddressAsync(s, "Bourla Salehi");
        var agent = await TestActors.StaffAsync(s.Db, "uakin");
        var closedId = await s.Db.TicketStatuses.Where(x => x.Key == "closed").Select(x => x.Id).SingleAsync();
        await s.Get<ITicketService>().TransitionStatusAsync(ticket.Id, closedId, agent);

        var messageId = await s.Get<IMailThreadTokenService>()
            .CreateMessageIdAsync(ticket.Id, "rapidsol.com.tr");
        var result = await ProcessAsync(s, Mail(owner, "destek@rapidsol.com.tr",
            "Re: hâlâ sorun var", "sorun devam ediyor", customize: m => m.InReplyTo = messageId));

        Assert.Equal(EmailInboundStatus.ThreadAppended, result.Status);
        var reopened = await s.Db.Tickets.Include(t => t.Status).SingleAsync(t => t.Id == ticket.Id);
        Assert.Equal(TicketState.Open, reopened.Status!.State);
        Assert.NotNull(reopened.ReopenedAt);
        Assert.Null(reopened.ClosedAt);
    }

    [Fact]
    public async Task Reply_FromStaffAddress_PostsAsResponse()
    {
        using var s = new ServiceScopeBundle(fixture);
        var ticket = await OwnTicketAsync(s);
        var staffEmail = await s.Db.Staff.Where(x => x.Username == "uakin" && x.Email != null)
            .Select(x => x.Email!).SingleAsync();
        var messageId = await s.Get<IMailThreadTokenService>()
            .CreateMessageIdAsync(ticket.Id, "rapidsol.com.tr");

        var result = await ProcessAsync(s, Mail(staffEmail, "destek@rapidsol.com.tr",
            "Re: konu", "Merhaba, inceliyoruz.", customize: m => m.InReplyTo = messageId));

        Assert.Equal(EmailInboundStatus.ThreadAppended, result.Status);
        var entry = await s.Db.ThreadEntries.SingleAsync(e => e.Id == result.ThreadEntryId);
        Assert.Equal(ThreadEntryType.Response, entry.Type);
        Assert.NotNull(entry.StaffId);
    }

    // ---- quoted-reply stripping ----------------------------------------------------------

    [Fact]
    public async Task StripQuoted_CutsAtSeparator_AndCanBeTurnedOff()
    {
        using var s0 = new ServiceScopeBundle(fixture);
        var ticket = await OwnTicketAsync(s0);
        var owner = await UserAddressAsync(s0, "Bourla Salehi");
        var separator = (await s0.Get<ISettingsService>().GetEmailAsync()).ReplySeparator;
        var text = $"Yeni cevabım burada.\n\n{separator}\n> eski mesaj gövdesi\n> ikinci satır";
        var mid = await s0.Get<IMailThreadTokenService>().CreateMessageIdAsync(ticket.Id, "rapidsol.com.tr");

        await using (await SettingOverride.SetAsync(fixture, "email", "strip_quoted", "true"))
        {
            using var s = new ServiceScopeBundle(fixture);
            var result = await ProcessAsync(s, Mail(owner, "destek@rapidsol.com.tr",
                "Re: konu", text, customize: m => m.InReplyTo = mid));
            var entry = await s.Db.ThreadEntries.SingleAsync(e => e.Id == result.ThreadEntryId);
            Assert.Contains("Yeni cevabım", entry.Body);
            Assert.DoesNotContain("eski mesaj gövdesi", entry.Body);
        }

        await using (await SettingOverride.SetAsync(fixture, "email", "strip_quoted", "false"))
        {
            using var s = new ServiceScopeBundle(fixture);
            var result = await ProcessAsync(s, Mail(owner, "destek@rapidsol.com.tr",
                "Re: konu", text, customize: m => m.InReplyTo = mid));
            var entry = await s.Db.ThreadEntries.SingleAsync(e => e.Id == result.ThreadEntryId);
            Assert.Contains("eski mesaj gövdesi", entry.Body);
        }
    }

    [Fact]
    public void StripQuoted_TextHeuristics_AreConservative()
    {
        // Separator cut wins.
        Assert.Equal("cevap", QuotedReplyStripper.StripText("cevap\n--- ayraç ---\n> eski", "--- ayraç ---"));
        // "Original Message" marker.
        Assert.Equal("cevap",
            QuotedReplyStripper.StripText("cevap\n----- Original Message -----\neski gövde", null));
        // Trailing ">" block + its attribution line.
        Assert.Equal("cevap",
            QuotedReplyStripper.StripText("cevap\n1 Oca 2020 tarihinde Ali yazdı:\n> eski\n> satır", null));
        // A quote-only body keeps the original (never strip to nothing).
        Assert.Equal("> sadece alıntı", QuotedReplyStripper.StripText("> sadece alıntı", null));
        // Mid-body quote the author replied UNDER stays intact.
        var underneath = "> soru\nyanıtım burada";
        Assert.Equal(underneath, QuotedReplyStripper.StripText(underneath, null));
    }

    [Fact]
    public void StripQuoted_HtmlHeuristics_DropTrailingBlockquote()
    {
        const string html = "<p>yeni cevap</p><blockquote>eski mesaj</blockquote>";
        Assert.Equal("<p>yeni cevap</p>", QuotedReplyStripper.StripHtml(html, null));
        // Text after the quote = the author wrote below it; keep everything.
        const string below = "<blockquote>eski</blockquote><p>altına yazdım</p>";
        Assert.Equal(below, QuotedReplyStripper.StripHtml(below, null));
    }

    // ---- protection gates ------------------------------------------------------------------

    [Fact]
    public async Task LoopTaggedMail_IsSkipped()
    {
        using var s = new ServiceScopeBundle(fixture);
        var address = $"dongu{Guid.NewGuid():N}"[..12] + "@disariden.example";

        // Below the threshold: still processed (one pass through the helpdesk is normal).
        var onePass = await ProcessAsync(s, Mail(address, "destek@rapidsol.com.tr", "tek geçiş", "gövde",
            customize: m => m.Headers.Add(MailPipelineHeaders.LoopTag, "1")));
        Assert.Equal(EmailInboundStatus.TicketCreated, onePass.Status);

        var looped = await ProcessAsync(s, Mail(address, "destek@rapidsol.com.tr", "döngü", "gövde",
            customize: m =>
            {
                for (var i = 0; i < MailPipelineHeaders.MaxPasses; i++)
                    m.Headers.Add(MailPipelineHeaders.LoopTag, "1");
            }));
        Assert.Equal(EmailInboundStatus.Skipped, looped.Status);
        Assert.Equal("loop", looped.Reason);
    }

    [Fact]
    public async Task MailFromOurOwnAddress_IsSkipped()
    {
        using var s = new ServiceScopeBundle(fixture);
        var result = await ProcessAsync(s, Mail("bilgi@rapidsol.com.tr", "destek@rapidsol.com.tr",
            "kendi kendine", "gövde"));

        Assert.Equal(EmailInboundStatus.Skipped, result.Status);
        Assert.Equal("self-mail", result.Reason);
    }

    [Theory]
    [InlineData("Auto-Submitted", "auto-replied")]
    [InlineData("Precedence", "bulk")]
    [InlineData("X-Auto-Response-Suppress", "DR, RN, OOF, AutoReply")]
    public async Task AutoSubmittedMail_IsSkipped(string header, string value)
    {
        using var s = new ServiceScopeBundle(fixture);
        var address = $"oto{Guid.NewGuid():N}"[..12] + "@disariden.example";

        var result = await ProcessAsync(s, Mail(address, "destek@rapidsol.com.tr", "Ofis dışındayım",
            "otomatik yanıt", customize: m => m.Headers.Add(header, value)));

        Assert.Equal(EmailInboundStatus.Skipped, result.Status);
        Assert.Equal("auto-submitted", result.Reason);
        Assert.False(await s.Db.Tickets.AnyAsync(t => t.Subject == "Ofis dışındayım"));
    }

    [Fact]
    public async Task AutoSubmittedNo_IsNotSkipped()
    {
        using var s = new ServiceScopeBundle(fixture);
        var address = $"insan{Guid.NewGuid():N}"[..12] + "@disariden.example";
        var result = await ProcessAsync(s, Mail(address, "destek@rapidsol.com.tr", "Elle yazıldı", "gövde",
            customize: m => m.Headers.Add("Auto-Submitted", "no")));
        Assert.Equal(EmailInboundStatus.TicketCreated, result.Status);
    }

    [Fact]
    public async Task BounceNotice_IsSkipped_AndFlagsTheOutboundRow()
    {
        using var s = new ServiceScopeBundle(fixture);
        var ticket = await OwnTicketAsync(s);
        var bouncedMid = $"RD1-{ticket.Id}-abcd-deadbeef1234@rapidsol.com.tr";
        var row = new EmailOutbound
        {
            ToAddress = "yok@disariden.example", Subject = "Bildirim", HtmlBody = "<p>x</p>",
            TicketId = ticket.Id, MessageId = bouncedMid, Status = EmailOutboundStatus.Sent,
            SentAt = DateTimeOffset.UtcNow,
        };
        s.Db.EmailOutbounds.Add(row);
        await s.Db.SaveChangesAsync();

        var dsn = new MimeMessage();
        dsn.From.Add(MailboxAddress.Parse("mailer-daemon@disariden.example"));
        dsn.To.Add(MailboxAddress.Parse("destek@rapidsol.com.tr"));
        dsn.Subject = "Undelivered Mail Returned to Sender";
        dsn.MessageId = $"{Guid.NewGuid():N}@disariden.example";
        var status = new MessageDeliveryStatus();
        status.StatusGroups.Add(new HeaderList { { "Final-Recipient", "rfc822; yok@disariden.example" },
            { "Action", "failed" }, { "Status", "5.1.1" } });
        var original = new MimeMessage();
        original.From.Add(MailboxAddress.Parse("destek@rapidsol.com.tr"));
        original.To.Add(MailboxAddress.Parse("yok@disariden.example"));
        original.MessageId = bouncedMid;
        original.Body = new TextPart("plain") { Text = "orijinal" };
        dsn.Body = new MultipartReport("delivery-status")
        {
            new TextPart("plain") { Text = "Delivery failed" },
            status,
            new MessagePart { Message = original },
        };

        var result = await ProcessAsync(s, dsn);

        Assert.Equal(EmailInboundStatus.Skipped, result.Status);
        Assert.Equal("bounce", result.Reason);
        // Never a ticket from a bounce; the correlated outbox row is honestly Failed.
        Assert.Null(result.TicketId);
        var flagged = await s.Db.EmailOutbounds.AsNoTracking().SingleAsync(o => o.Id == row.Id);
        Assert.Equal(EmailOutboundStatus.Failed, flagged.Status);
        Assert.Contains("bounce", flagged.LastError);
    }

    [Fact]
    public async Task BannedSender_IsRejected()
    {
        var address = $"engelli{Guid.NewGuid():N}"[..14] + "@disariden.example";
        using (var setup = new ServiceScopeBundle(fixture))
        {
            setup.Db.BanlistEntries.Add(new BanlistEntry { Address = address, IsActive = true });
            await setup.Db.SaveChangesAsync();
        }
        try
        {
            using var s = new ServiceScopeBundle(fixture);
            var result = await ProcessAsync(s, Mail(address, "destek@rapidsol.com.tr", "spam", "gövde"));

            Assert.Equal(EmailInboundStatus.Rejected, result.Status);
            Assert.Equal("banlist", result.Reason);
            Assert.False(await s.Db.Tickets.AnyAsync(t => t.Subject == "spam"));
        }
        finally
        {
            using var cleanup = new ServiceScopeBundle(fixture);
            await cleanup.Db.BanlistEntries.Where(b => b.Address == address).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task FilterRejectAction_OnEmailTarget_DropsTheMail()
    {
        var marker = $"REDDET{Guid.NewGuid():N}"[..12];
        int filterId, accountId;
        using (var setup = new ServiceScopeBundle(fixture))
        {
            accountId = (await MailboxAsync(setup)).Account.Id;
            var filter = new Filter
            {
                Name = $"Posta reddi {marker}",
                Target = FilterTarget.Email,
                EmailAccountId = accountId,
                IsActive = true,
                MatchAllRules = true,
                ExecOrder = 1,
                Rules = [new FilterRule { What = "subject", How = FilterMatchHow.Contains, Value = marker, IsActive = true }],
                Actions = [new FilterAction { Type = "reject", Sort = 1 }],
            };
            setup.Db.Filters.Add(filter);
            await setup.Db.SaveChangesAsync();
            filterId = filter.Id;
        }
        try
        {
            using var s = new ServiceScopeBundle(fixture);
            var address = $"filtre{Guid.NewGuid():N}"[..12] + "@disariden.example";

            var rejected = await ProcessAsync(s, Mail(address, "destek@rapidsol.com.tr",
                $"konu {marker}", "gövde"));
            Assert.Equal(EmailInboundStatus.Rejected, rejected.Status);
            Assert.StartsWith("filter:", rejected.Reason);

            // Same sender, non-matching subject → normal create (the target gate is
            // Email + this account, so web creates are untouched by construction).
            var allowed = await ProcessAsync(s, Mail(address, "destek@rapidsol.com.tr", "temiz konu", "gövde"));
            Assert.Equal(EmailInboundStatus.TicketCreated, allowed.Status);
        }
        finally
        {
            using var cleanup = new ServiceScopeBundle(fixture);
            await cleanup.Db.Filters.Where(f => f.Id == filterId).ExecuteDeleteAsync();
        }
    }

    // ---- collaborators ----------------------------------------------------------------------

    [Fact]
    public async Task CcRecipients_BecomeCollaborators_WhenAutoAddCollabsOn()
    {
        using var s = new ServiceScopeBundle(fixture);
        var sender = $"gonderen{Guid.NewGuid():N}"[..14] + "@disariden.example";
        var cc = $"kopya{Guid.NewGuid():N}"[..12] + "@disariden.example";

        var result = await ProcessAsync(s, Mail(sender, "destek@rapidsol.com.tr", "Ortak konu", "gövde",
            customize: m => m.Cc.Add(MailboxAddress.Parse(cc))));

        Assert.Equal(EmailInboundStatus.TicketCreated, result.Status);
        var threadId = await s.Db.Tickets.Where(t => t.Id == result.TicketId).Select(t => t.ThreadId).SingleAsync();
        var collaborators = await s.Db.ThreadCollaborators.Include(c => c.User)
            .Where(c => c.ThreadId == threadId)
            .SelectMany(c => c.User!.Emails.Select(e => e.Address)).ToListAsync();
        Assert.Contains(cc, collaborators);
        // Our own helpdesk address (the To) is never a collaborator.
        Assert.DoesNotContain("destek@rapidsol.com.tr", collaborators);
    }

    [Fact]
    public async Task CcRecipients_AreIgnored_WhenAutoAddCollabsOff()
    {
        await using var off = await SettingOverride.SetAsync(fixture, "email", "auto_add_collabs", "false");
        using var s = new ServiceScopeBundle(fixture);
        var sender = $"tek{Guid.NewGuid():N}"[..12] + "@disariden.example";
        var cc = $"yoksay{Guid.NewGuid():N}"[..12] + "@disariden.example";

        var result = await ProcessAsync(s, Mail(sender, "destek@rapidsol.com.tr", "Yalnız konu", "gövde",
            customize: m => m.Cc.Add(MailboxAddress.Parse(cc))));

        var threadId = await s.Db.Tickets.Where(t => t.Id == result.TicketId).Select(t => t.ThreadId).SingleAsync();
        Assert.False(await s.Db.ThreadCollaborators.AnyAsync(c => c.ThreadId == threadId));
    }

    // ---- fetch job orchestration ------------------------------------------------------------

    [Fact]
    public async Task FetchJob_ProcessesMessages_AndAppliesPostFetchAction()
    {
        int channelId;
        PostFetchAction original;
        using (var setup = new ServiceScopeBundle(fixture))
        {
            var (_, channel) = await MailboxAsync(setup);
            channelId = channel.Id;
            original = channel.PostFetch;
            channel.PostFetch = PostFetchAction.Archive;
            channel.ArchiveFolder = "Arsiv";
            channel.LastActivityAt = null;
            await setup.Db.SaveChangesAsync();
        }
        try
        {
            var client = new FakeInboundMailClient();
            var address = $"getir{Guid.NewGuid():N}"[..12] + "@disariden.example";
            client.Messages.Add(("101", Mail(address, "destek@rapidsol.com.tr", "Getirilen konu", "gövde")));

            using (var s = new ServiceScopeBundle(fixture))
                await RunFetchAsync(s, client);

            using var check = new ServiceScopeBundle(fixture);
            Assert.Equal(1, client.Connects);
            // The channel's persisted post-fetch action was applied honestly.
            Assert.Equal(("101", PostFetchAction.Archive, "Arsiv"), client.PostFetched.Single());
            // The credential/host settings crossed the seam.
            Assert.Equal("imap.rapidsol.com.tr", client.LastConnection!.Host);
            Assert.Equal(MailProtocol.Imap, client.LastConnection.Protocol);

            var ticket = await check.Db.Tickets.SingleAsync(t => t.Subject == "Getirilen konu");
            Assert.Equal(TicketSource.Email, ticket.Source);
            // Success bookkeeping on the channel.
            var channel = await check.Db.Set<EmailChannel>().SingleAsync(c => c.Id == channelId);
            Assert.Equal(Now, channel.LastActivityAt);
            Assert.Equal(0, channel.ErrorCount);
        }
        finally
        {
            using var cleanup = new ServiceScopeBundle(fixture);
            var channel = await cleanup.Db.Set<EmailChannel>().SingleAsync(c => c.Id == channelId);
            channel.PostFetch = original;
            channel.ArchiveFolder = null;
            channel.LastActivityAt = null;
            channel.ErrorCount = 0;
            channel.LastErrorMessage = null;
            await cleanup.Db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task FetchJob_IsIdempotent_AcrossRepeatedFetches()
    {
        int channelId;
        using (var setup = new ServiceScopeBundle(fixture))
        {
            var (_, channel) = await MailboxAsync(setup);
            channelId = channel.Id;
            channel.LastActivityAt = null;
            await setup.Db.SaveChangesAsync();
        }
        try
        {
            var client = new FakeInboundMailClient();
            var address = $"tekrar{Guid.NewGuid():N}"[..12] + "@disariden.example";
            var subject = $"Tekrarlanan {Guid.NewGuid():N}"[..24];
            // Same message stays in the mailbox (post-fetch "Nothing" + a server that
            // keeps re-listing it): the second pass must not create a second ticket.
            client.Messages.Add(("202", Mail(address, "destek@rapidsol.com.tr", subject, "gövde")));

            using (var s = new ServiceScopeBundle(fixture))
                await RunFetchAsync(s, client);
            using (var reset = new ServiceScopeBundle(fixture))
            {
                (await reset.Db.Set<EmailChannel>().SingleAsync(c => c.Id == channelId)).LastActivityAt = null;
                await reset.Db.SaveChangesAsync();
            }
            using (var s = new ServiceScopeBundle(fixture))
                await RunFetchAsync(s, client, Now.AddMinutes(30));

            using var check = new ServiceScopeBundle(fixture);
            Assert.Equal(1, await check.Db.Tickets.CountAsync(t => t.Subject == subject));
            Assert.Equal(1, await check.Db.EmailInbounds.CountAsync(i => i.Subject == subject));
            // Both passes still applied the post-fetch action (the second is the
            // duplicate short-circuit — the message must still leave the inbox).
            Assert.Equal(2, client.PostFetched.Count);
        }
        finally
        {
            using var cleanup = new ServiceScopeBundle(fixture);
            (await cleanup.Db.Set<EmailChannel>().SingleAsync(c => c.Id == channelId)).LastActivityAt = null;
            await cleanup.Db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task FetchJob_RespectsFetchFrequency_AndMasterSwitches()
    {
        int channelId;
        using (var setup = new ServiceScopeBundle(fixture))
        {
            var (_, channel) = await MailboxAsync(setup);
            channelId = channel.Id;
            channel.LastActivityAt = null;
            await setup.Db.SaveChangesAsync();
        }
        try
        {
            var client = new FakeInboundMailClient();
            client.Messages.Add(("303", Mail("kimse@disariden.example", "destek@rapidsol.com.tr",
                "Kapalı anahtar", "gövde")));

            // email/fetch_enabled off → no connection at all.
            await using (await SettingOverride.SetAsync(fixture, "email", "fetch_enabled", "false"))
            {
                using var s = new ServiceScopeBundle(fixture);
                await RunFetchAsync(s, client);
            }
            Assert.Equal(0, client.Connects);

            // fetch_auto_cron off → the scheduled path stays quiet too.
            await using (await SettingOverride.SetAsync(fixture, "email", "fetch_auto_cron", "false"))
            {
                using var s = new ServiceScopeBundle(fixture);
                await RunFetchAsync(s, client);
            }
            Assert.Equal(0, client.Connects);

            // Both on → fetches once; a second tick inside FetchFrequencyMinutes is skipped.
            using (var s = new ServiceScopeBundle(fixture))
                await RunFetchAsync(s, client);
            using (var s = new ServiceScopeBundle(fixture))
                await RunFetchAsync(s, client, Now.AddSeconds(30));
            Assert.Equal(1, client.Connects);
        }
        finally
        {
            using var cleanup = new ServiceScopeBundle(fixture);
            (await cleanup.Db.Set<EmailChannel>().SingleAsync(c => c.Id == channelId)).LastActivityAt = null;
            await cleanup.Db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task FetchJob_ConnectFailure_RecordsChannelError_AndSyslogs()
    {
        int channelId;
        using (var setup = new ServiceScopeBundle(fixture))
        {
            var (_, channel) = await MailboxAsync(setup);
            channelId = channel.Id;
            channel.LastActivityAt = null;
            await setup.Db.SaveChangesAsync();
        }
        try
        {
            var client = new FakeInboundMailClient { ConnectFailure = new IOException("bağlantı reddedildi") };
            using (var s = new ServiceScopeBundle(fixture))
                await RunFetchAsync(s, client);

            using var check = new ServiceScopeBundle(fixture);
            var channel = await check.Db.Set<EmailChannel>().SingleAsync(c => c.Id == channelId);
            Assert.Equal(1, channel.ErrorCount);
            Assert.Equal("bağlantı reddedildi", channel.LastErrorMessage);
            Assert.NotNull(channel.LastErrorAt);
            Assert.True(await check.Db.SystemLogEntries
                .AnyAsync(e => e.Type == SystemLogType.Error && e.Log == "bağlantı reddedildi"));
        }
        finally
        {
            using var cleanup = new ServiceScopeBundle(fixture);
            var channel = await cleanup.Db.Set<EmailChannel>().SingleAsync(c => c.Id == channelId);
            channel.LastActivityAt = null;
            channel.ErrorCount = 0;
            channel.LastErrorMessage = null;
            channel.LastErrorAt = null;
            await cleanup.Db.SaveChangesAsync();
        }
    }

    // ---- outbound stamping (the other half of the threading contract) ------------------------

    [Fact]
    public async Task OutboundTicketMail_CarriesSignedMessageId_AndLoopHeaderStamp()
    {
        using var s = new ServiceScopeBundle(fixture);
        var ticket = await OwnTicketAsync(s);
        var destekId = await s.Db.EmailAccounts.Where(a => a.Address == "destek@rapidsol.com.tr")
            .Select(a => a.Id).SingleAsync();
        var to = $"alici{Guid.NewGuid():N}"[..12] + "@disariden.example";

        var row = await s.Get<IMailQueue>().EnqueueAsync(
            new OutboundEmailRequest(to, "Konu", "<p>gövde</p>")
            { FromEmailAccountId = destekId, TicketId = ticket.Id });

        var stored = await s.Db.EmailOutbounds.AsNoTracking().SingleAsync(o => o.Id == row.Id);
        Assert.NotNull(stored.MessageId);
        // The stamped id decodes back to the ticket (the inbound matcher's contract).
        Assert.Equal(ticket.Id, await s.Get<IMailThreadTokenService>()
            .TryDecodeTicketIdAsync(stored.MessageId!));
        var send = fixture.Factory.Transport.Sent.Single(t => t.To == to);
        Assert.Equal(stored.MessageId, send.MessageId);
    }
}
