using Microsoft.EntityFrameworkCore;
using MimeKit;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S8 slice 5 — the agent reply mail (osTicket <c>Ticket::postReply</c>) driven through
/// the REAL pipeline: IThreadService post → ThreadEntryAdded → TicketMailHandler →
/// IMailQueue → inline send job → capturing SMTP transport. Also covers the RFC 3834
/// Auto-Submitted split (human replies carry no header, notifications do) and the
/// round trip that matters most: the customer answering our reply lands on the SAME
/// ticket via the signed Message-Id.
/// </summary>
[Collection("Postgres")]
public class ReplyMailTests(PostgresFixture fixture)
{
    private const string OwnerName = "Bourla Salehi";
    private const string OwnerEmail = "bourla.salehi@ulasim.com.tr";

    private async Task<Ticket> OwnTicketAsync(ServiceScopeBundle s)
    {
        var owner = await TestActors.UserAsync(s.Db, OwnerName);
        return await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Yanıt testi {Guid.NewGuid():N}"[..24],
            Body = "<p>ilk mesaj</p>",
        }, owner);
    }

    /// <summary>Posts a staff Response exactly like the agent composer does.</summary>
    private static Task<ThreadEntry> ReplyAsync(ServiceScopeBundle s, Ticket ticket, ActorContext agent,
        string body, string? signature = null, int? fromAccountId = null) =>
        s.Get<IThreadService>().PostAsync(ticket.ThreadId, ThreadEntryType.Response, body, agent,
            new PostOptions
            {
                Format = "text",
                SignatureText = signature,
                FromEmailAccountId = fromAccountId,
            });

    private static CapturedSmtpSend ReplySend(PostgresFixture fixture) =>
        fixture.Factory.Transport.Sent.Single(t => t.Subject == "Yanıt Şablonu");

    // ---- the reply itself ---------------------------------------------------------------

    [Fact]
    public async Task StaffResponse_MailsTheOwner_FromTheDeptAccount_WithSignatureAndReplyHeaders()
    {
        using var s = new ServiceScopeBundle(fixture);
        var ticket = await OwnTicketAsync(s);
        var agent = await TestActors.StaffAsync(s.Db, "uakin");
        fixture.Factory.Transport.Clear();
        fixture.Factory.Emails.Clear();

        await ReplyAsync(s, ticket, agent, "Kaydınızı güncelledik.", signature: "Umut Akın\nDestek Uzmanı");

        var send = ReplySend(fixture);
        // Recipient + template + the reply body as %{response}.
        Assert.Equal(OwnerEmail, send.To);
        Assert.Contains(ticket.Number, send.HtmlBody);
        Assert.Contains("Kaydınızı güncelledik.", send.HtmlBody);
        // The composer's signature choice actually renders (ROADMAP NOTE(S8)).
        Assert.Contains("Umut Akın", send.HtmlBody);
        Assert.Contains("Destek Uzmanı", send.HtmlBody);
        // Department's outgoing identity (seeded destek@ on the default department).
        Assert.Equal("destek@rapidsol.com.tr", send.FromAddress);
        // Reply-To points at the fetched mailbox, and the Message-Id is our signed token.
        Assert.Equal("destek@rapidsol.com.tr", send.ReplyTo);
        Assert.NotNull(send.MessageId);
        Assert.StartsWith($"{MailThreadTokenService.Prefix}-{ticket.Id}-", send.MessageId);
        Assert.Equal(ticket.Id, await s.Get<IMailThreadTokenService>().TryDecodeTicketIdAsync(send.MessageId!));

        // Durable outbox row, marked human-authored and linked to the entry.
        var row = await s.Db.EmailOutbounds.AsNoTracking()
            .SingleAsync(o => o.TicketId == ticket.Id && o.Subject == "Yanıt Şablonu");
        Assert.False(row.IsAutomated);
        Assert.Equal(EmailOutboundStatus.Sent, row.Status);
        Assert.NotNull(row.ThreadEntryId);
    }

    [Fact]
    public async Task StaffResponse_UsesTheComposersFromAccount_AndOmitsAnUnchosenSignature()
    {
        using var s = new ServiceScopeBundle(fixture);
        var ticket = await OwnTicketAsync(s);
        var agent = await TestActors.StaffAsync(s.Db, "uakin");
        var bordroId = (await s.Db.EmailAccounts.SingleAsync(a => a.Address == "bordro@rapidsol.com.tr")).Id;
        fixture.Factory.Transport.Clear();

        // "Farklı bir adresten gönder" wins over the department's account…
        await ReplyAsync(s, ticket, agent, "Bordro ekibinden yanıt", fromAccountId: bordroId);

        var row = await s.Db.EmailOutbounds.AsNoTracking()
            .SingleAsync(o => o.TicketId == ticket.Id && o.Subject == "Yanıt Şablonu");
        Assert.Equal(bordroId, row.FromEmailAccountId);
        // …and "İmza yok" leaves no dangling separator in the body.
        Assert.DoesNotContain("--<br>", row.HtmlBody);
    }

    [Fact]
    public async Task StaffResponse_CopiesActiveCollaborators_ButNotInactiveOnes()
    {
        using var s = new ServiceScopeBundle(fixture);
        var ticket = await OwnTicketAsync(s);
        var agent = await TestActors.StaffAsync(s.Db, "uakin");
        var threads = s.Get<IThreadService>();

        // Two collaborators on the thread; one is then deactivated.
        var active = await s.Get<IUserService>().CreateAsync(
            new UserCreateRequest { Name = "Katılımcı Aktif", Email = $"kat{Guid.NewGuid():N}"[..12] + "@ulasim.com.tr" },
            ActorContext.System);
        var muted = await s.Get<IUserService>().CreateAsync(
            new UserCreateRequest { Name = "Katılımcı Pasif", Email = $"pas{Guid.NewGuid():N}"[..12] + "@ulasim.com.tr" },
            ActorContext.System);
        await threads.AddCollaboratorAsync(ticket.ThreadId, active.Id, CollaboratorRole.Cc, ActorContext.System);
        var mutedRow = await threads.AddCollaboratorAsync(ticket.ThreadId, muted.Id, CollaboratorRole.Cc, ActorContext.System);
        mutedRow.IsActive = false;
        await s.Db.SaveChangesAsync();
        fixture.Factory.Transport.Clear();

        await ReplyAsync(s, ticket, agent, "Herkese bilgi");

        // One send, owner in To, the ACTIVE collaborator in Cc (osTicket MailingList).
        var send = ReplySend(fixture);
        Assert.Equal(OwnerEmail, send.To);
        Assert.Contains(active.Emails[0].Address, send.Cc);
        Assert.DoesNotContain(muted.Emails[0].Address, send.Cc ?? "");
    }

    [Fact]
    public async Task StaffResponse_AttachesFiles_OnlyWhenAttachmentsInEmailIsOn()
    {
        int threadId;
        using (var setup = new ServiceScopeBundle(fixture))
            threadId = (await OwnTicketAsync(setup)).ThreadId;

        // Each leg runs in its OWN scope: SettingsService memoizes per scope, so a
        // flip must be observed by a freshly resolved handler graph.
        async Task<IReadOnlyList<string>?> PostAndCaptureAsync()
        {
            using var s = new ServiceScopeBundle(fixture);
            var agent = await TestActors.StaffAsync(s.Db, "uakin");
            fixture.Factory.Transport.Clear();
            await s.Get<IThreadService>().PostAsync(threadId, ThreadEntryType.Response,
                "Ekli raporu inceleyin", agent,
                new PostOptions
                {
                    Format = "text",
                    OnPosted = async (entry, ct) =>
                    {
                        using var content = new MemoryStream("rapor"u8.ToArray());
                        var stored = await s.Get<Domain.Services.IFileStore>()
                            .SaveAsync(content, "rapor.txt", "text/plain", ct);
                        s.Db.StoredFiles.Add(stored);
                        s.Db.Attachments.Add(new Attachment
                        {
                            ObjectType = AttachmentObjectType.ThreadEntry,
                            ObjectId = entry.Id,
                            File = stored,
                        });
                        await s.Db.SaveChangesAsync(ct);
                    },
                });
            return ReplySend(fixture).Attachments;
        }

        // Default (email/attachments_in_email on): the reply carries the file out
        // (osTicket emailAttachments → $response->getAttachments()).
        Assert.Equal(["rapor.txt"], await PostAndCaptureAsync());

        // Switched off: the same reply goes out bare, the file stays on the ticket.
        await using (await SettingOverride.SetAsync(fixture, "email", "attachments_in_email", "false"))
        {
            Assert.Empty(await PostAndCaptureAsync() ?? []);
        }
    }

    // ---- Auto-Submitted split (RFC 3834) ------------------------------------------------

    [Fact]
    public async Task AutoSubmitted_IsStampedOnNotifications_AndAbsentOnAgentReplies()
    {
        using var s = new ServiceScopeBundle(fixture);
        fixture.Factory.Transport.Clear();

        // A ticket create fans out machine-written mail…
        var ticket = await OwnTicketAsync(s);
        var notification = fixture.Factory.Transport.Sent
            .First(t => t.To == OwnerEmail && t.Subject != "Yanıt Şablonu");
        Assert.True(notification.AutoSubmitted);

        // …the agent's own words do not get told "do not reply".
        var agent = await TestActors.StaffAsync(s.Db, "uakin");
        fixture.Factory.Transport.Clear();
        await ReplyAsync(s, ticket, agent, "Elimizde, dönüş yapacağız.");
        Assert.False(ReplySend(fixture).AutoSubmitted);
    }

    // ---- the round trip (mini exit gate) ------------------------------------------------

    [Fact]
    public async Task CustomerReplyingToTheReplyMail_AppendsToTheSameTicket()
    {
        using var s = new ServiceScopeBundle(fixture);
        var ticket = await OwnTicketAsync(s);
        var agent = await TestActors.StaffAsync(s.Db, "uakin");
        fixture.Factory.Transport.Clear();

        await ReplyAsync(s, ticket, agent, "Bilgi için teşekkürler, kontrol ediyoruz.");
        var ourMessageId = ReplySend(fixture).MessageId;
        Assert.NotNull(ourMessageId);

        // The customer hits Reply: their client echoes our Message-Id in In-Reply-To.
        var answer = new MimeMessage();
        answer.From.Add(MailboxAddress.Parse(OwnerEmail));
        answer.To.Add(MailboxAddress.Parse("destek@rapidsol.com.tr"));
        answer.Subject = "Re: yanıt";
        answer.MessageId = $"{Guid.NewGuid():N}@ulasim.com.tr";
        answer.InReplyTo = ourMessageId;
        answer.Body = new TextPart("plain") { Text = "Teşekkürler, bekliyorum." };

        var account = await s.Db.EmailAccounts.Include(a => a.Channels)
            .SingleAsync(a => a.Address == "destek@rapidsol.com.tr");
        var result = await s.Get<InboundMailProcessor>().ProcessAsync(
            account, account.Channels.Single(c => c.Kind == EmailChannelKind.Mailbox),
            Guid.NewGuid().ToString("n")[..10], answer);

        Assert.Equal(EmailInboundStatus.ThreadAppended, result.Status);
        Assert.Equal(ticket.Id, result.TicketId);
        var appended = await s.Db.ThreadEntries.AsNoTracking().SingleAsync(e => e.Id == result.ThreadEntryId);
        Assert.Equal(ThreadEntryType.Message, appended.Type);
        Assert.Contains("bekliyorum", appended.Body);
    }
}
