using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Tests;

/// <summary>
/// S8 outbound mail core: template renderer substitution, transport selection
/// precedence, the persistent outbox state machine (job invoked directly — no
/// Hangfire server in tests) and the effort emails through the REAL pipeline
/// (queue → inline job → capturing SMTP transport).
/// </summary>
[Collection("Postgres")]
public class MailPipelineTests(PostgresFixture fixture)
{
    private async Task<(int TicketId, string Number)> CreateTicketAsync(ServiceScopeBundle s, string? subject = null)
    {
        var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
        var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = subject ?? $"Posta testi {Guid.NewGuid():N}",
            Body = "<p>Test içeriği</p>",
        }, owner);
        return (ticket.Id, ticket.Number);
    }

    private async Task<int> AccountIdAsync(ServiceScopeBundle s, string address) =>
        (await s.Db.EmailAccounts.SingleAsync(a => a.Address == address)).Id;

    private static async Task<EmailOutbound> NewRowAsync(ServiceScopeBundle s, int? fromAccountId, string? to = null)
    {
        var row = new EmailOutbound
        {
            ToAddress = to ?? $"out{Guid.NewGuid():N}"[..14] + "@example.com",
            Subject = "Konu",
            HtmlBody = "<p>Gövde</p>",
            FromEmailAccountId = fromAccountId,
        };
        s.Db.EmailOutbounds.Add(row);
        await s.Db.SaveChangesAsync();
        return row;
    }

    // ---- renderer ---------------------------------------------------------------------

    [Fact]
    public async Task Renderer_Substitutes_Encodes_Links_And_DropsUnknowns()
    {
        int setId;
        using (var setup = new ServiceScopeBundle(fixture))
        {
            var set = new EmailTemplateSet
            {
                Name = $"Render testi {Guid.NewGuid():N}"[..30],
                Language = "tr",
                IsActive = false, // selected explicitly via default_template_set_id
                Templates =
                [
                    new EmailTemplate
                    {
                        CodeName = "ticket.notice",
                        Subject = "[#%{ticket.number}] %{ticket.subject}",
                        Body = "<p>%{ticket.subject}</p><p>%{company.name}</p>"
                            + "<p><a href=\"%{ticket.link}\">bağlantı</a></p>"
                            + "<p>%{recipient.name} / %{recipient.email}</p>"
                            + "<p>[%{unknown.variable}]</p>%{message}",
                    },
                ],
            };
            setup.Db.EmailTemplateSets.Add(set);
            await setup.Db.SaveChangesAsync();
            setId = set.Id;
        }

        await using var setOverride = await SettingOverride.SetAsync(fixture, "email", "default_template_set_id", setId.ToString());
        await using var urlOverride = await SettingOverride.SetAsync(fixture, "core", "helpdesk_url", "https://destek.example.com/");
        await using var companyOverride = await SettingOverride.SetAsync(fixture, "company", "name", "Ünlü & Şirket <A.Ş.>");
        try
        {
            using var s = new ServiceScopeBundle(fixture);
            var (ticketId, number) = await CreateTicketAsync(s, subject: "Bordro <b>acil & önemli</b>");

            var rendered = await s.Get<IEmailTemplateRenderer>().RenderAsync("ticket.notice", new EmailRenderContext
            {
                TicketId = ticketId,
                RecipientName = "Bourla Salehi",
                RecipientEmail = "bourla.salehi@ulasim.com.tr",
                MessageHtml = "<em>zaten temiz HTML</em>",
            });

            Assert.NotNull(rendered);
            // Subject is plain text: raw values, no HTML encoding.
            Assert.Equal($"[#{number}] Bordro <b>acil & önemli</b>", rendered.Subject);
            // Body is HTML: plain values encoded…
            Assert.Contains("Bordro &lt;b&gt;acil &amp; önemli&lt;/b&gt;", rendered.HtmlBody);
            Assert.Contains("Ünlü &amp; Şirket &lt;A.Ş.&gt;", rendered.HtmlBody);
            // …the link builds on core/helpdesk_url (trailing slash normalized)…
            Assert.Contains($"https://destek.example.com/ticket-view?id={ticketId}", rendered.HtmlBody);
            Assert.Contains("Bourla Salehi / bourla.salehi@ulasim.com.tr", rendered.HtmlBody);
            // …unknown variables render empty (never leak), content HTML passes raw.
            Assert.Contains("<p>[]</p>", rendered.HtmlBody);
            Assert.DoesNotContain("%{", rendered.HtmlBody);
            Assert.Contains("<em>zaten temiz HTML</em>", rendered.HtmlBody);

            // Missing template code → null (caller logs and skips).
            Assert.Null(await s.Get<IEmailTemplateRenderer>().RenderAsync("no.such.template", new EmailRenderContext()));
        }
        finally
        {
            using var cleanup = new ServiceScopeBundle(fixture);
            await cleanup.Db.EmailTemplateSets.Where(t => t.Id == setId).ExecuteDeleteAsync();
        }
    }

    // ---- transport selection ----------------------------------------------------------

    [Fact]
    public async Task TransportSelection_ExplicitAccount_WinsAndSetsFromHeader()
    {
        using var s = new ServiceScopeBundle(fixture);
        var destekId = await AccountIdAsync(s, "destek@rapidsol.com.tr");
        var row = await NewRowAsync(s, destekId);

        await s.Get<OutboundMailJob>().SendAsync(row.Id, CancellationToken.None);

        Assert.Equal(EmailOutboundStatus.Sent, row.Status);
        Assert.NotNull(row.SentAt);
        var send = fixture.Factory.Transport.Sent.Single(t => t.To == row.ToAddress);
        Assert.Equal("destek@rapidsol.com.tr", send.FromAddress);
        Assert.Equal("RapidSol Destek", send.FromName); // account display name, not helpdesk_title
    }

    [Fact]
    public async Task TransportSelection_DefaultSmtpAccount_ThenSystemDefault()
    {
        int destekId;
        using (var s0 = new ServiceScopeBundle(fixture))
            destekId = await AccountIdAsync(s0, "destek@rapidsol.com.tr");

        // email/default_smtp = an account id.
        await using (await SettingOverride.SetAsync(fixture, "email", "default_smtp", destekId.ToString()))
        {
            using var s = new ServiceScopeBundle(fixture);
            var row = await NewRowAsync(s, fromAccountId: null);
            await s.Get<OutboundMailJob>().SendAsync(row.Id, CancellationToken.None);
            Assert.Equal(EmailOutboundStatus.Sent, row.Status);
            Assert.Equal("destek@rapidsol.com.tr",
                fixture.Factory.Transport.Sent.Single(t => t.To == row.ToAddress).FromAddress);
        }

        // email/default_smtp = "system" → email/default_email_id decides.
        await using (await SettingOverride.SetAsync(fixture, "email", "default_smtp", "system"))
        await using (await SettingOverride.SetAsync(fixture, "email", "default_email_id", destekId.ToString()))
        {
            using var s = new ServiceScopeBundle(fixture);
            var row = await NewRowAsync(s, fromAccountId: null);
            await s.Get<OutboundMailJob>().SendAsync(row.Id, CancellationToken.None);
            Assert.Equal(EmailOutboundStatus.Sent, row.Status);
            Assert.Equal("destek@rapidsol.com.tr",
                fixture.Factory.Transport.Sent.Single(t => t.To == row.ToAddress).FromAddress);
        }
    }

    [Fact]
    public async Task TransportSelection_NoUsableAccount_DevFallbackMarksSentWithNote()
    {
        using var s = new ServiceScopeBundle(fixture);
        // bordro@ exists but has NO SMTP channel; the settings defaults are unset —
        // no usable transport anywhere → the Development fallback logs it.
        var bordroId = await AccountIdAsync(s, "bordro@rapidsol.com.tr");
        var row = await NewRowAsync(s, bordroId);

        await s.Get<OutboundMailJob>().SendAsync(row.Id, CancellationToken.None);

        Assert.Equal(EmailOutboundStatus.Sent, row.Status);
        Assert.Contains("dev-fallback", row.LastError);
        Assert.DoesNotContain(fixture.Factory.Transport.Sent, t => t.To == row.ToAddress); // never hit SMTP
        Assert.Contains(fixture.Factory.Emails.Sent, m => m.To == row.ToAddress);          // captured fallback
    }

    // ---- outbox state machine -----------------------------------------------------------

    [Fact]
    public async Task Queue_FailureIncrementsAttempts_RetrySucceeds_ExhaustionFails()
    {
        using var s = new ServiceScopeBundle(fixture);
        var destekId = await AccountIdAsync(s, "destek@rapidsol.com.tr");
        var job = s.Get<OutboundMailJob>();

        // Failure: attempts + typed LastError recorded, row back to Pending, and the
        // job throws so Hangfire's AutomaticRetry reschedules it.
        var row = await NewRowAsync(s, destekId);
        fixture.Factory.Transport.ScriptNextResult(MailTestResult.Fail("send", "boom"));
        await Assert.ThrowsAsync<MailSendException>(() => job.SendAsync(row.Id, CancellationToken.None));
        Assert.Equal(EmailOutboundStatus.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Equal("send: boom", row.LastError);

        // Retry (unscripted transport succeeds) → Sent, error cleared.
        await job.SendAsync(row.Id, CancellationToken.None);
        Assert.Equal(EmailOutboundStatus.Sent, row.Status);
        Assert.Equal(2, row.Attempts);
        Assert.Null(row.LastError);
        Assert.NotNull(row.SentAt);

        // A Sent row is never re-sent (job replay is a no-op).
        await job.SendAsync(row.Id, CancellationToken.None);
        Assert.Equal(2, row.Attempts);

        // Final allowed execution failing → Failed without a throw (no more retries).
        var doomed = await NewRowAsync(s, destekId);
        doomed.Attempts = OutboundMailJob.MaxAttempts - 1;
        await s.Db.SaveChangesAsync();
        fixture.Factory.Transport.ScriptNextResult(MailTestResult.Fail("connect", "down"));
        await job.SendAsync(doomed.Id, CancellationToken.None);
        Assert.Equal(EmailOutboundStatus.Failed, doomed.Status);
        Assert.Equal(OutboundMailJob.MaxAttempts, doomed.Attempts);
        Assert.Equal("connect: down", doomed.LastError);
    }

    [Fact]
    public async Task Enqueue_PersistsRow_SchedulesJob_AndSendCompletes()
    {
        using var s = new ServiceScopeBundle(fixture);
        var destekId = await AccountIdAsync(s, "destek@rapidsol.com.tr");
        var to = $"q{Guid.NewGuid():N}"[..12] + "@example.com";

        var row = await s.Get<IMailQueue>().EnqueueAsync(
            new OutboundEmailRequest(to, "Kuyruk testi", "<p>gövde</p>") { FromEmailAccountId = destekId });

        // The Hangfire client saw exactly this row's send job…
        Assert.Contains(fixture.Factory.Jobs.Created,
            j => j.Type == typeof(OutboundMailJob) && j.Method == "SendAsync" && Equals(j.Args[0], row.Id));
        // …and the inline execution (worker parity) completed the send.
        var stored = await s.Db.EmailOutbounds.AsNoTracking().SingleAsync(o => o.Id == row.Id);
        Assert.Equal(EmailOutboundStatus.Sent, stored.Status);
        Assert.Equal(to, fixture.Factory.Transport.Sent.Single(t => t.To == to).To);
    }

    [Fact]
    public async Task QueueBackedAppSender_RidesTheQueue()
    {
        using var s = new ServiceScopeBundle(fixture);
        var to = $"a{Guid.NewGuid():N}"[..12] + "@example.com";

        // The production IAppEmailSender implementation (page tests still capture at
        // the interface seam — this exercises the real wiring underneath).
        await new QueueBackedEmailSender(s.Get<IMailQueue>()).SendAsync(to, "Hesap postası", "<p>bağlantı</p>");

        var row = await s.Db.EmailOutbounds.SingleAsync(o => o.ToAddress == to);
        Assert.Null(row.FromEmailAccountId); // settings default decides at send time
        Assert.Equal(EmailOutboundStatus.Sent, row.Status); // dev fallback (no default SMTP configured)
        Assert.Contains(fixture.Factory.Emails.Sent, m => m.To == to && m.Subject == "Hesap postası");
    }

    // ---- effort emails through the real pipeline ----------------------------------------

    [Fact]
    public async Task EffortRequest_RendersCatalogTemplate_ThroughQueueAndTransport()
    {
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, number) = await CreateTicketAsync(s);
        var agent = await TestActors.StaffAsync(s.Db, "uakin");
        var destekId = await AccountIdAsync(s, "destek@rapidsol.com.tr");
        fixture.Factory.Emails.Clear();
        fixture.Factory.Transport.Clear(); // earlier tests also sent effort mails

        await s.Get<IEffortProposalService>().ProposeAsync(ticketId, 6, "Test kapsamı", agent);

        // The queued send crossed the SMTP transport seam with the department's
        // from-address and the rendered catalog template (stock body carries the
        // substituted ticket number).
        var send = fixture.Factory.Transport.Sent.Single(t => t.To == "bourla.salehi@ulasim.com.tr" && t.Subject == "Efor Onayı İsteği");
        Assert.Equal("destek@rapidsol.com.tr", send.FromAddress);
        Assert.Contains(number, send.HtmlBody);

        // Durable outbox row: linked to the ticket, from the dept account, Sent.
        // (S8 slice 2: the create itself also queues autoresponse/alert rows for
        // this ticket — filter to the effort request.)
        var row = await s.Db.EmailOutbounds.SingleAsync(o => o.TicketId == ticketId && o.Subject == "Efor Onayı İsteği");
        Assert.Equal(EmailOutboundStatus.Sent, row.Status);
        Assert.Equal(destekId, row.FromEmailAccountId);
        Assert.Equal("Efor Onayı İsteği", row.Subject);
        Assert.NotNull(row.SentAt);
    }
}
