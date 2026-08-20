using System.Net;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Services;

namespace RapidsolDestek.Web.Services;

/// <summary>
/// S8 slice 5 — the two account mails that carry a one-shot token: registration
/// verification and the guest → portal-account invitation.
///
/// Verification renders the <c>user.confirm.email</c> system template (the row the
/// settings-users page already edits, in both languages — exactly what the ROADMAP's
/// email_verify note anticipated). The invitation has NO template in the mockup canon,
/// so its body is composed here, following the check-status access-link precedent —
/// flagged: it joins the template catalog when a canon row for it exists.
///
/// Both ride <see cref="IMailQueue"/> like every other outbound mail, and both are
/// automated by definition (nobody wrote the prose), so the Auto-Submitted header
/// stays on them.
/// </summary>
public interface IPortalAccountMailer
{
    Task SendVerificationAsync(string toAddress, string name, string link, CancellationToken ct = default);

    Task SendInviteAsync(string toAddress, string name, string link, CancellationToken ct = default);
}

public sealed class PortalAccountMailer(
    ISystemTemplateService templates,
    ISettingsService settings,
    IMailQueue queue) : IPortalAccountMailer
{
    public async Task SendVerificationAsync(string toAddress, string name, string link, CancellationToken ct = default)
    {
        var row = (await templates.GetAsync(["user.confirm.email"], ct)).FirstOrDefault();
        var body = Expand(row?.BodyTr ?? "Merhaba %{user.name},\n\nHesabınızı doğrulamak için bağlantıya tıklayın: %{link}",
            name, link);
        await queue.EnqueueAsync(new OutboundEmailRequest(toAddress, await SubjectAsync(ct), body), ct);
    }

    public async Task SendInviteAsync(string toAddress, string name, string link, CancellationToken ct = default)
    {
        var body = Expand(
            "Merhaba %{user.name},\n\nRapidsolDestek portalında sizin için bir hesap açıldı. "
            + "Parolanızı belirleyip hesabınızı etkinleştirmek için bağlantıya tıklayın: %{link}",
            name, link);
        await queue.EnqueueAsync(new OutboundEmailRequest(toAddress, await SubjectAsync(ct), body), ct);
    }

    /// <summary>Subject = the helpdesk title (the account-mail convention already used
    /// by the check-status access link).</summary>
    private async Task<string> SubjectAsync(CancellationToken ct) =>
        await settings.GetAsync("core", "helpdesk_title", ct) ?? "RapidsolDestek";

    /// <summary>System-template bodies are plain text with %{user.name} / %{link}
    /// pills; the link becomes a real anchor, everything else is encoded.</summary>
    private static string Expand(string body, string name, string link)
    {
        var encoded = string.Join("<br>",
            body.Replace("\r\n", "\n").Split('\n').Select(WebUtility.HtmlEncode));
        return TemplateVariableExpander.Expand(encoded, new Dictionary<string, string?>
        {
            ["user.name"] = WebUtility.HtmlEncode(name),
            ["link"] = $"<a href=\"{WebUtility.HtmlEncode(link)}\">{WebUtility.HtmlEncode(link)}</a>",
        });
    }
}
