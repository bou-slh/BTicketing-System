using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using Testcontainers.PostgreSql;

namespace RapidsolDestek.E2E;

/// <summary>
/// S8 EXIT GATE — the staging round trip against a REAL mail server:
/// customer mail → ticket → autoresponse → agent reply → customer reply → thread append.
///
/// Nothing on the mail path is stubbed. A GreenMail container speaks real SMTP (3025)
/// and IMAP (3143); the app under test boots the real <c>Program</c> in-process against
/// a throwaway Postgres container and keeps its production mail wiring —
/// <see cref="MailKitSmtpTransport"/> submits, <see cref="MailKitInboundMailClient"/>
/// fetches, the DataProtection-encrypted channel secrets are decrypted for real (the
/// test asserts the concrete types are in play, so a future DI change that swaps in a
/// fake fails the gate loudly). The customer is an ordinary MUA: a bare MailKit
/// <see cref="SmtpClient"/> submitting to the same server, and an
/// <see cref="ImapClient"/> reading their own mailbox back.
///
/// The only substitutions are the SCHEDULER, not the transports: Hangfire's server is
/// off and <see cref="InlineJobClient"/> runs enqueued jobs inline, and MailFetchJob is
/// driven directly with a controllable clock instead of waiting on its one-minute cron.
/// Both keep the test deterministic without weakening a single mail seam.
///
/// Fixture data is built here, never in the seeder: the seeded destek@ account is
/// repointed at the container and its autoresponse/reply templates are given
/// placeholder-carrying subjects so substitution is observable in a delivered mail.
/// </summary>
[Trait("Category", "E2E")]
public sealed class MailRoundTripTests : IAsyncLifetime
{
    // The helpdesk address is the seeded canon one, so department routing, the
    // self-mail gate and Reply-To all line up with the real configuration.
    private const string HelpdeskAddress = "destek@rapidsol.com.tr";
    private const string HelpdeskLogin = "destek";        // GreenMail logins are the local part
    private const string HelpdeskPassword = "gm-destek-1";
    private const string CustomerAddress = "musteri@ornek.com.tr";
    private const string CustomerLogin = "musteri";
    private const string CustomerPassword = "gm-musteri-1";
    private const string CustomerName = "Ayşe Yılmaz";

    /// <summary>GreenMail's plain SMTP/IMAP ports advertise no STARTTLS, so the
    /// transports' <see cref="SecureSocketOptions.Auto"/> negotiates cleartext —
    /// no connect-path change is needed to talk to the container.</summary>
    private const ushort GreenMailSmtp = 3025;
    private const ushort GreenMailImap = 3143;

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("rapidsoldestek_mailgate")
        .WithUsername("rapidsol")
        .WithPassword("rapidsol-test")
        .Build();

    private readonly IContainer _mail = new ContainerBuilder("greenmail/standalone:2.1.12")
        .WithEnvironment("GREENMAIL_OPTS",
            "-Dgreenmail.setup.test.all -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.verbose "
            + $"-Dgreenmail.users={HelpdeskLogin}:{HelpdeskPassword}@rapidsol.com.tr,"
            + $"{CustomerLogin}:{CustomerPassword}@ornek.com.tr")
        .WithPortBinding(GreenMailSmtp, true)
        .WithPortBinding(GreenMailImap, true)
        // The mailbox users are created AFTER "Started services"; the API-server line
        // is the first one logged past user creation.
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Starting GreenMail API server"))
        .Build();

    private MailGateFactory _app = null!;
    private string MailHost => _mail.Hostname;
    private int SmtpPort => _mail.GetMappedPublicPort(GreenMailSmtp);
    private int ImapPort => _mail.GetMappedPublicPort(GreenMailImap);

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_db.StartAsync(), _mail.StartAsync());
        _app = new MailGateFactory(_db.GetConnectionString());
        _ = _app.Services; // boots Program (Development): migrate + Identity/Domain seed
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        await _mail.DisposeAsync();
        await _db.DisposeAsync();
    }

    // ---- host ---------------------------------------------------------------------------

    /// <summary>The real Program with production mail wiring intact. Only the job
    /// scheduler is swapped (Hangfire server off + inline execution).</summary>
    private sealed class MailGateFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.UseSetting("Hangfire:ServerEnabled", "false");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<InlineJobClient>();
                services.AddSingleton<IBackgroundJobClient>(sp => sp.GetRequiredService<InlineJobClient>());
            });
        }
    }

    /// <summary>Executes enqueued jobs inline in a fresh scope, like a Hangfire worker,
    /// so a queued send has really left over SMTP by the time the enqueuer returns.</summary>
    private sealed class InlineJobClient(IServiceProvider services) : IBackgroundJobClient
    {
        public string Create(Job job, IState state)
        {
            if (state is EnqueuedState)
            {
                using var scope = services.CreateScope();
                var instance = ActivatorUtilities.GetServiceOrCreateInstance(scope.ServiceProvider, job.Type);
                var result = job.Method.Invoke(instance, [.. job.Args]);
                (result as Task)?.GetAwaiter().GetResult();
            }
            return Guid.NewGuid().ToString("n");
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---- the customer's mail client (an ordinary MUA, no app code involved) ---------------

    private async Task MuaSendAsync(MimeMessage message)
    {
        using var client = new SmtpClient();
        await client.ConnectAsync(MailHost, SmtpPort, SecureSocketOptions.Auto);
        await client.AuthenticateAsync(CustomerLogin, CustomerPassword);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    /// <summary>Everything currently sitting in the customer's mailbox.</summary>
    private async Task<List<MimeMessage>> CustomerInboxAsync()
    {
        using var client = new ImapClient();
        await client.ConnectAsync(MailHost, ImapPort, SecureSocketOptions.Auto);
        await client.AuthenticateAsync(CustomerLogin, CustomerPassword);
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);
        var messages = new List<MimeMessage>();
        foreach (var uid in await client.Inbox.SearchAsync(SearchQuery.All))
            messages.Add(await client.Inbox.GetMessageAsync(uid));
        await client.DisconnectAsync(true);
        return messages;
    }

    /// <summary>Polls the customer's real mailbox until the expected mail is delivered
    /// (SMTP submission and GreenMail's local delivery are separate steps).</summary>
    private async Task<MimeMessage> WaitForCustomerMailAsync(Func<MimeMessage, bool> match, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        List<MimeMessage> inbox;
        do
        {
            inbox = await CustomerInboxAsync();
            if (inbox.FirstOrDefault(match) is { } hit)
                return hit;
            await Task.Delay(250);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Fail($"{what} never reached {CustomerAddress}. Mailbox held: "
            + string.Join(" | ", inbox.Select(m => $"'{m.Subject}' from {m.From}")));
        return null!; // unreachable
    }

    // ---- the fetch pass (real IMAP, controllable clock) -----------------------------------

    /// <summary>Runs MailFetchJob once over the real MailKit IMAP client. The clock is
    /// ours only so the channel's five-minute cadence does not silently skip the second
    /// pass — every other collaborator is the app's own registration.</summary>
    private async Task RunFetchAsync(DateTimeOffset now)
    {
        using var scope = _app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var inbound = sp.GetRequiredService<IInboundMailClient>();
        Assert.IsType<MailKitInboundMailClient>(inbound); // no fake mailbox in this gate
        var job = new MailFetchJob(
            sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<ISettingsService>(),
            inbound,
            sp.GetRequiredService<IMailCredentialResolver>(),
            sp.GetRequiredService<IMailOAuthTokenService>(),
            sp.GetRequiredService<InboundMailProcessor>(),
            sp.GetRequiredService<ISystemLogService>(),
            new FixedClock(now),
            NullLogger<MailFetchJob>.Instance);
        await job.RunAsync(CancellationToken.None);
    }

    // ---- fixture: point the seeded helpdesk account at the container ----------------------

    /// <summary>
    /// Repoints the seeded destek@ account's mailbox + SMTP channels at GreenMail
    /// (basic auth, secret stored through the real DataProtection protector), turns the
    /// fetch master switches on, and gives the two customer-facing templates
    /// placeholder-carrying subjects so substitution is provable from a delivered mail.
    /// </summary>
    private async Task<(int AccountId, int DepartmentId, int TopicId)> ArrangeHelpdeskAsync()
    {
        using var scope = _app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var secrets = sp.GetRequiredService<RapidsolDestek.Web.Services.IEmailSecretProtector>();
        var settings = sp.GetRequiredService<ISettingsService>();

        var account = await db.EmailAccounts.Include(a => a.Channels)
            .SingleAsync(a => a.Address == HelpdeskAddress);
        // Route through the account's help-topic override. The create cascade is
        // filter → topic → account (TicketService), so the ticket must land in the
        // TOPIC's department — a different one from the account's own "Destek", which
        // is what makes the assertion prove routing rather than a default.
        var topic = await db.HelpTopics.SingleAsync(h => h.Name == "Bordro");
        var routedDepartmentId = topic.DepartmentId!.Value;
        account.HelpTopicId = topic.Id;
        account.NoAutoResponse = false;

        // That department's outgoing identity is this mailbox, so every mail of the
        // round trip is submitted by the account under test instead of falling through
        // the transport-default chain.
        var routedDepartment = await db.Departments.SingleAsync(d => d.Id == routedDepartmentId);
        routedDepartment.EmailAccountId = account.Id;
        routedDepartment.AutoResponseEmailAccountId = account.Id;
        routedDepartment.TicketAutoResponse = true;

        var mailbox = account.Channels.Single(c => c.Kind == EmailChannelKind.Mailbox);
        mailbox.IsActive = true;
        mailbox.Protocol = MailProtocol.Imap;
        mailbox.AuthKind = MailAuthKind.Basic;
        mailbox.Encryption = MailEncryption.None;
        mailbox.Host = MailHost;
        mailbox.Port = ImapPort;
        mailbox.Folder = "INBOX";
        mailbox.Username = HelpdeskLogin;
        mailbox.PasswordProtected = secrets.Protect(HelpdeskPassword);
        mailbox.FetchFrequencyMinutes = 1;
        mailbox.FetchMax = 30;
        mailbox.PostFetch = PostFetchAction.Nothing; // mark seen — GreenMail has no Arsiv/ tree
        mailbox.ArchiveFolder = null;
        mailbox.LastActivityAt = null;

        var smtp = account.Channels.Single(c => c.Kind == EmailChannelKind.Smtp);
        smtp.IsActive = true;
        smtp.Protocol = MailProtocol.Smtp;
        smtp.AuthKind = MailAuthKind.Basic;
        smtp.Encryption = MailEncryption.None;
        smtp.Host = MailHost;
        smtp.Port = SmtpPort;
        smtp.Username = HelpdeskLogin;
        smtp.PasswordProtected = secrets.Protect(HelpdeskPassword);

        // Any other seeded mailbox channel would have the fetch job dialling a real
        // host; this gate owns the whole pass.
        foreach (var other in await db.Set<EmailChannel>()
            .Where(c => c.Kind == EmailChannelKind.Mailbox && c.EmailAccountId != account.Id)
            .ToListAsync())
        {
            other.IsActive = false;
        }

        var setId = await db.EmailTemplateSets.Where(s => s.IsActive && s.Language == "tr")
            .OrderBy(s => s.Id).Select(s => s.Id).FirstAsync();
        var set = await db.EmailTemplateSets.Include(s => s.Templates).SingleAsync(s => s.Id == setId);
        var autoresp = set.Templates.Single(t => t.CodeName == "ticket.autoresp");
        autoresp.Subject = "[%{ticket.number}] Talebiniz alındı — %{ticket.dept.name}";
        autoresp.Body = "<p>Sayın %{recipient.name}, %{ticket.number} numaralı talebiniz alınmıştır.</p>"
            + "<p>Konu: %{ticket.subject}</p>";
        var replyTemplate = set.Templates.Single(t => t.CodeName == "ticket.reply");
        // Deliberately NO "#number" token: the subject last-resort matcher needs a '#',
        // so the customer's answer can only thread through the signed Message-Id.
        replyTemplate.Subject = "[%{ticket.number}] Talebiniz hakkında";

        await db.SaveChangesAsync();

        await settings.SetAsync("email", "fetch_enabled", "true");
        await settings.SetAsync("email", "fetch_auto_cron", "true");
        await settings.SetAsync("email", "default_email_id", account.Id.ToString());
        await settings.SetAsync("email", "default_smtp", "system");
        await settings.SetAsync("email", "strip_quoted", "true");

        // The gate is worthless if a fake slipped into the graph.
        Assert.IsType<MailKitSmtpTransport>(sp.GetRequiredService<ISmtpMailTransport>());
        Assert.IsType<MailKitInboundMailClient>(sp.GetRequiredService<IInboundMailClient>());

        return (account.Id, routedDepartmentId, topic.Id);
    }

    // ---- the gate -------------------------------------------------------------------------

    [Fact]
    public async Task StagingRoundTrip_OverRealSmtpAndImap_MailBecomesTicket_ReplyComesBack_AnswerAppends()
    {
        var (accountId, departmentId, topicId) = await ArrangeHelpdeskAsync();
        var start = DateTimeOffset.UtcNow;

        // ---- 1. the customer mails the helpdesk, as any MUA would --------------------
        const string customerSubject = "Bordro görüntüleme hatası";
        const string customerText =
            "Merhaba,\n\nBordro ekranını açtığımda hata alıyorum. Şifremi de değiştiremiyorum.\n\n"
            + "İyi çalışmalar,\nAyşe";
        var first = new MimeMessage();
        first.From.Add(new MailboxAddress(CustomerName, CustomerAddress));
        first.To.Add(MailboxAddress.Parse(HelpdeskAddress));
        first.Subject = customerSubject;
        first.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("ornek.com.tr");
        first.Body = new TextPart("plain") { Text = customerText };
        await MuaSendAsync(first);

        await RunFetchAsync(start);

        int ticketId, threadId, ticketCountAfterCreate;
        string ticketNumber;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var inboundRow = await db.EmailInbounds.AsNoTracking()
                .SingleOrDefaultAsync(i => i.MessageId == first.MessageId);
            Assert.NotNull(inboundRow);
            Assert.Equal(EmailInboundStatus.TicketCreated, inboundRow!.Status);

            var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == inboundRow.TicketId);
            ticketId = ticket.Id;
            threadId = ticket.ThreadId;
            ticketNumber = ticket.Number;
            Assert.Equal(customerSubject, ticket.Subject);
            Assert.Equal(TicketSource.Email, ticket.Source);
            Assert.Equal(topicId, ticket.HelpTopicId);          // account's help-topic override
            Assert.Equal(departmentId, ticket.DepartmentId);    // …and the topic's department
            Assert.Equal("Bordro", await db.Departments.Where(d => d.Id == ticket.DepartmentId)
                .Select(d => d.Name).SingleAsync());
            Assert.Equal(accountId, ticket.EmailAccountId);     // arrived on our address

            var owner = await db.Users.AsNoTracking().Include(u => u.Emails)
                .SingleAsync(u => u.Id == ticket.UserId);
            Assert.Equal(CustomerName, owner.Name);
            Assert.Contains(owner.Emails, e => e.Address == CustomerAddress);

            var entry = await db.ThreadEntries.AsNoTracking()
                .Where(e => e.ThreadId == threadId && e.Type == ThreadEntryType.Message)
                .OrderBy(e => e.Id).FirstAsync();
            // The stored body is HTML built from the plain part, so WebUtility's
            // numeric escapes for the Latin-1 letters (ö/ü/ç) are expected there;
            // decoding must give the customer's text back exactly — that is what
            // proves the UTF-8/transfer-encoding path, not merely "some text".
            var storedText = WebUtility.HtmlDecode(entry.Body.Replace("<br>", "\n"));
            Assert.Contains("Bordro ekranını açtığımda hata alıyorum.", storedText);
            Assert.Contains("Şifremi de değiştiremiyorum.", storedText);
            Assert.Contains("İyi çalışmalar", storedText);
            // No mojibake either: letters outside Latin-1 survive literally as stored.
            Assert.Contains("Şifremi de değiştiremiyorum.", entry.Body);

            ticketCountAfterCreate = await db.Tickets.CountAsync();
        }

        // ---- 2. the autoresponse really lands in the customer's mailbox ---------------
        var autoresponse = await WaitForCustomerMailAsync(
            m => (m.Subject ?? "").Contains("Talebiniz alındı"), "The new-ticket autoresponse");
        Assert.Equal($"[{ticketNumber}] Talebiniz alındı — Bordro", autoresponse.Subject); // subject substitution
        var sender = Assert.Single(autoresponse.From.Mailboxes);
        Assert.Equal(HelpdeskAddress, sender.Address);
        Assert.Equal("RapidSol Destek", sender.Name);
        Assert.Equal(CustomerAddress, Assert.Single(autoresponse.To.Mailboxes).Address);
        var autoHtml = autoresponse.HtmlBody;
        Assert.Contains($"Sayın {CustomerName}", autoHtml);                   // %{recipient.name}
        Assert.Contains($"{ticketNumber} numaralı talebiniz", autoHtml);      // %{ticket.number}
        Assert.Contains($"Konu: {customerSubject}", autoHtml);                // %{ticket.subject}
        // Slice 5's renderer fix: plain values are markup-encoded only, so Turkish
        // reaches the customer as letters, never as numeric character references.
        Assert.DoesNotContain("&#", autoHtml);
        // RFC 3834: an automated message says so.
        Assert.Equal("auto-generated", autoresponse.Headers[HeaderId.AutoSubmitted]);

        // ---- 3. an agent replies; the mail reaches the customer -----------------------
        const string agentText = "Merhaba Ayşe Hanım, bordro görüntüleme hatasını çözdük; "
            + "şifre sıfırlama bağlantısını da gönderdik.";
        using (var scope = _app.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<AppDbContext>();
            var staff = await db.Staff.SingleAsync(s => s.Username == "uakin");
            // Service level (not the HTTP composer): the same IThreadService call the
            // agent reply endpoint makes, without an auth/CSRF round trip in the way.
            await sp.GetRequiredService<IThreadService>().PostAsync(
                threadId, ThreadEntryType.Response, agentText, ActorContext.ForStaff(staff),
                new PostOptions { Format = "text" });
        }

        var reply = await WaitForCustomerMailAsync(
            m => (m.Subject ?? "").Contains("Talebiniz hakkında"), "The agent reply mail");
        Assert.Equal($"[{ticketNumber}] Talebiniz hakkında", reply.Subject);
        Assert.Equal(HelpdeskAddress, Assert.Single(reply.From.Mailboxes).Address);
        Assert.Contains(agentText, reply.HtmlBody);
        // Reply-To advertises the fetched mailbox, so the answer comes back to us.
        Assert.Equal(HelpdeskAddress, Assert.Single(reply.ReplyTo.Mailboxes).Address);
        // The Message-Id is the signed reply token, and the app's own codec verifies it.
        Assert.NotNull(reply.MessageId);
        Assert.StartsWith($"{MailThreadTokenService.Prefix}-{ticketId}-", reply.MessageId);
        using (var scope = _app.Services.CreateScope())
        {
            var decoded = await scope.ServiceProvider.GetRequiredService<IMailThreadTokenService>()
                .TryDecodeTicketIdAsync(reply.MessageId!);
            Assert.Equal(ticketId, decoded);
        }
        // Our reply references the customer's mail, so their client threads it.
        Assert.Equal(first.MessageId, reply.InReplyTo);
        // RFC 3834 again, the other way round (S8 slice 5): human prose carries NO
        // Auto-Submitted — stamping it would tell the customer's mail system not to
        // answer the very message that asks for an answer.
        Assert.Null(reply.Headers[HeaderId.AutoSubmitted]);

        // ---- 4. the customer replies to that mail; it appends to the SAME ticket ------
        var answer = new MimeMessage();
        answer.From.Add(new MailboxAddress(CustomerName, CustomerAddress));
        answer.To.Add(MailboxAddress.Parse(HelpdeskAddress)); // what Reply-To told them
        answer.Subject = "Re: " + reply.Subject;
        answer.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("ornek.com.tr");
        answer.InReplyTo = reply.MessageId;
        answer.References.Add(reply.MessageId!);
        answer.Body = new TextPart("plain")
        {
            Text = "Teşekkür ederim, şimdi çalışıyor.\n\n"
                + "20 Ağu 2026 tarihinde RapidSol Destek <destek@rapidsol.com.tr> şunu yazdı:\n"
                + "> Merhaba Ayşe Hanım, bordro görüntüleme hatasını çözdük;\n"
                + "> şifre sıfırlama bağlantısını da gönderdik.\n",
        };
        await MuaSendAsync(answer);

        // A later clock tick — the channel's cadence has elapsed since pass one.
        await RunFetchAsync(start.AddMinutes(10));

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var appendRow = await db.EmailInbounds.AsNoTracking()
                .SingleOrDefaultAsync(i => i.MessageId == answer.MessageId);
            Assert.NotNull(appendRow);
            Assert.Equal(EmailInboundStatus.ThreadAppended, appendRow!.Status);
            Assert.Equal(ticketId, appendRow.TicketId);
            // No second ticket was opened by the reply.
            Assert.Equal(ticketCountAfterCreate, await db.Tickets.CountAsync());

            var appended = await db.ThreadEntries.AsNoTracking()
                .SingleAsync(e => e.Id == appendRow.ThreadEntryId);
            Assert.Equal(threadId, appended.ThreadId);
            Assert.Equal(ThreadEntryType.Message, appended.Type);
            var appendedText = WebUtility.HtmlDecode(appended.Body.Replace("<br>", "\n"));
            Assert.Contains("Teşekkür ederim, şimdi çalışıyor.", appendedText);
            // The quoted original (and its attribution line) is stripped.
            Assert.DoesNotContain("şunu yazdı:", appendedText);
            Assert.DoesNotContain("şifre sıfırlama bağlantısını da gönderdik", appendedText);

            // Every send of this round trip is a Sent outbox row over the real transport.
            var outbox = await db.EmailOutbounds.AsNoTracking()
                .Where(o => o.TicketId == ticketId).ToListAsync();
            Assert.All(outbox, o => Assert.Equal(EmailOutboundStatus.Sent, o.Status));
            Assert.Contains(outbox, o => o.IsAutomated && o.Subject.Contains("Talebiniz alındı"));
            Assert.Contains(outbox, o => !o.IsAutomated && o.Subject.Contains("Talebiniz hakkında"));

            // And the fetch channel is healthy: two clean passes, no error bookkeeping.
            var channel = await db.Set<EmailChannel>().AsNoTracking()
                .SingleAsync(c => c.EmailAccountId == accountId && c.Kind == EmailChannelKind.Mailbox);
            Assert.Equal(0, channel.ErrorCount);
            Assert.Null(channel.LastErrorMessage);
        }
    }
}
