using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Tests;

/// <summary>
/// S8 slice 5 — the three signed-link flows the mail pipeline carries: the
/// users/auth_tokens ticket auto-login link, registration verification, and the guest
/// invitation. Token semantics (scope, expiry, single use, purpose isolation) are
/// exercised on <see cref="IMailLinkTokenService"/>; the mails themselves are asserted
/// where they land — the capturing sender behind the real queue.
/// </summary>
[Collection("Postgres")]
public class MailTokenFlowTests(PostgresFixture fixture)
{
    private static async Task<User> GuestAsync(ServiceScopeBundle s, string prefix) =>
        await s.Get<IUserService>().CreateAsync(new UserCreateRequest
        {
            Name = $"{prefix} Misafir",
            Email = $"{prefix}{Guid.NewGuid():N}"[..14] + "@ulasim.com.tr",
        }, ActorContext.System);

    // ---- auth_tokens: the ticket link inside customer mail -------------------------------

    [Fact]
    public async Task TicketLink_CarriesAnAccessToken_OnlyWhileAuthTokensIsOn()
    {
        // Settings memoize per scope (SettingsService), so each flip gets its own.
        await using (await SettingOverride.SetAsync(fixture, "users", "auth_tokens", "false"))
        {
            using var off = new ServiceScopeBundle(fixture);
            var plain = await off.Get<IMailLinkTokenService>().TicketLinkAsync(42, "https://destek.example.com/");
            Assert.Equal("https://destek.example.com/ticket-view?id=42", plain);
        }

        await using (await SettingOverride.SetAsync(fixture, "users", "auth_tokens", "true"))
        {
            using var on = new ServiceScopeBundle(fixture);
            var signed = await on.Get<IMailLinkTokenService>().TicketLinkAsync(42, "https://destek.example.com/");
            Assert.StartsWith("https://destek.example.com/ticket-view?id=42&token=", signed);
            Assert.True(signed.Length > "https://destek.example.com/ticket-view?id=42&token=".Length + 20);
        }
    }

    [Fact]
    public async Task ReplyMail_EmbedsTheAutoLoginLink_WhenAuthTokensIsOn()
    {
        int setId;
        using (var setup = new ServiceScopeBundle(fixture))
        {
            // A reply template that actually places the recipient link pill.
            var set = new EmailTemplateSet
            {
                Name = $"Auth link {Guid.NewGuid():N}"[..28],
                Language = "tr",
                IsActive = false,
                Templates =
                [
                    new EmailTemplate
                    {
                        CodeName = "ticket.reply",
                        Subject = "Yanıt %{ticket.number}",
                        Body = "<p>%{response}</p><p><a href=\"%{recipient.ticket_link}\">talep</a></p>",
                    },
                ],
            };
            setup.Db.EmailTemplateSets.Add(set);
            await setup.Db.SaveChangesAsync();
            setId = set.Id;
        }

        try
        {
            await using var setOverride = await SettingOverride.SetAsync(fixture, "email", "default_template_set_id", setId.ToString());
            await using var authOverride = await SettingOverride.SetAsync(fixture, "users", "auth_tokens", "true");

            using var s = new ServiceScopeBundle(fixture);
            var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
            var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
            {
                UserId = owner.Id!.Value,
                Subject = $"Oto giriş {Guid.NewGuid():N}"[..22],
                Body = "<p>gövde</p>",
            }, owner);
            var agent = await TestActors.StaffAsync(s.Db, "uakin");
            fixture.Factory.Transport.Clear();

            await s.Get<IThreadService>().PostAsync(ticket.ThreadId, ThreadEntryType.Response,
                "Bağlantıdan takip edebilirsiniz.", agent, new PostOptions { Format = "text" });

            var send = fixture.Factory.Transport.Sent.Single(t => t.Subject == $"Yanıt {ticket.Number}");
            Assert.Contains($"ticket-view?id={ticket.Id}&amp;token=", send.HtmlBody);
        }
        finally
        {
            using var cleanup = new ServiceScopeBundle(fixture);
            await cleanup.Db.EmailTemplateSets.Where(t => t.Id == setId).ExecuteDeleteAsync();
        }
    }

    // ---- one-shot token semantics --------------------------------------------------------

    [Fact]
    public async Task OneShotToken_RedeemsOnce_ThenRefusesReplay()
    {
        using var s = new ServiceScopeBundle(fixture);
        var guest = await GuestAsync(s, "tek");
        var links = s.Get<IMailLinkTokenService>();

        var token = await links.IssueAsync(MailTokenPurpose.Invite, guest.Id, TimeSpan.FromDays(1));
        Assert.Equal(guest.Id, await links.PeekAsync(MailTokenPurpose.Invite, token));
        Assert.Equal(guest.Id, await links.RedeemAsync(MailTokenPurpose.Invite, token));
        // Second presentation of the SAME link buys nothing.
        Assert.Null(await links.RedeemAsync(MailTokenPurpose.Invite, token));
        Assert.Null(await links.PeekAsync(MailTokenPurpose.Invite, token));
    }

    [Fact]
    public async Task OneShotToken_RefusesExpired_WrongPurpose_AndTampered()
    {
        using var s = new ServiceScopeBundle(fixture);
        var guest = await GuestAsync(s, "sure");
        var links = s.Get<IMailLinkTokenService>();

        // Expired: the payload's own deadline is already behind us.
        var expired = await links.IssueAsync(MailTokenPurpose.EmailVerify, guest.Id, TimeSpan.FromSeconds(-1));
        Assert.Null(await links.RedeemAsync(MailTokenPurpose.EmailVerify, expired));

        // Wrong purpose: a verification link may not activate an invitation.
        var verify = await links.IssueAsync(MailTokenPurpose.EmailVerify, guest.Id, TimeSpan.FromDays(1));
        Assert.Null(await links.RedeemAsync(MailTokenPurpose.Invite, verify));
        // …and the refusal did not burn the real grant.
        Assert.Equal(guest.Id, await links.RedeemAsync(MailTokenPurpose.EmailVerify, verify));

        // Forged / truncated payloads never throw, they just do not resolve.
        Assert.Null(await links.RedeemAsync(MailTokenPurpose.Invite, "kesinlikle-gecerli-degil"));
        Assert.Null(await links.RedeemAsync(MailTokenPurpose.Invite, null));
    }

    [Fact]
    public async Task ReissuingAToken_InvalidatesThePreviousLink()
    {
        using var s = new ServiceScopeBundle(fixture);
        var guest = await GuestAsync(s, "yeni");
        var links = s.Get<IMailLinkTokenService>();

        var first = await links.IssueAsync(MailTokenPurpose.EmailVerify, guest.Id, TimeSpan.FromDays(1));
        var second = await links.IssueAsync(MailTokenPurpose.EmailVerify, guest.Id, TimeSpan.FromDays(1));

        Assert.Null(await links.RedeemAsync(MailTokenPurpose.EmailVerify, first));
        Assert.Equal(guest.Id, await links.RedeemAsync(MailTokenPurpose.EmailVerify, second));
    }

    // ---- the mails that carry them -------------------------------------------------------

    [Fact]
    public async Task VerificationMail_IsQueued_WithTheLink()
    {
        using var s = new ServiceScopeBundle(fixture);
        var guest = await GuestAsync(s, "dogrula");
        var address = guest.Emails[0].Address;
        var token = await s.Get<IMailLinkTokenService>().IssueAsync(
            MailTokenPurpose.EmailVerify, guest.Id, TimeSpan.FromDays(2));
        var link = $"https://destek.example.com/register/verify?token={Uri.EscapeDataString(token)}";
        fixture.Factory.Emails.Clear();

        await s.Get<IPortalAccountMailer>().SendVerificationAsync(address, guest.Name, link);

        var mail = fixture.Factory.Emails.Sent.Single(m => m.To == address);
        Assert.Contains("register/verify?token=", mail.HtmlBody);
        Assert.Contains(guest.Name, mail.HtmlBody);
        // Rode the real outbox, automated (nobody wrote the prose).
        var row = await s.Db.EmailOutbounds.AsNoTracking().SingleAsync(o => o.ToAddress == address);
        Assert.True(row.IsAutomated);
    }

    [Fact]
    public async Task InviteMail_IsQueued_WithTheActivationLink()
    {
        using var s = new ServiceScopeBundle(fixture);
        var guest = await GuestAsync(s, "davet");
        var address = guest.Emails[0].Address;
        var token = await s.Get<IMailLinkTokenService>().IssueAsync(
            MailTokenPurpose.Invite, guest.Id, MailLinkTokenService.InviteLifetime);
        fixture.Factory.Emails.Clear();

        await s.Get<IPortalAccountMailer>().SendInviteAsync(address, guest.Name,
            $"https://destek.example.com/invite?token={Uri.EscapeDataString(token)}");

        var mail = fixture.Factory.Emails.Sent.Single(m => m.To == address);
        Assert.Contains("invite?token=", mail.HtmlBody);
        // The ledger row is still unconsumed until the invitee actually uses it.
        var row = await s.Db.MailTokens.AsNoTracking()
            .Where(t => t.UserId == guest.Id && t.Purpose == MailTokenPurpose.Invite).SingleAsync();
        Assert.Null(row.ConsumedAt);
    }
}
