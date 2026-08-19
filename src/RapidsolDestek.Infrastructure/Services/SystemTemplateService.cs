using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Auditing;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>One system template as the settings pages' dlg-tpl edits it: a shared
/// display name (the EmailTemplate Subject of both language rows) plus one body per
/// template-set language (the dialog's Türkçe/English tabs).</summary>
public sealed record SystemTemplateRow(string Code, string Name, string BodyTr, string BodyEn);

public interface ISystemTemplateService
{
    /// <summary>Rows for the given codes in catalog order — DB truth where a row
    /// exists in the active tr/en sets, catalog defaults otherwise (no writes on read).</summary>
    Task<IReadOnlyList<SystemTemplateRow>> GetAsync(IReadOnlyList<string> codes, CancellationToken ct = default);

    /// <summary>Upserts one template into BOTH active sets (subject = the shared name,
    /// body per language). Throws <see cref="DomainRuleException"/> "template-unknown"
    /// for codes outside the catalog.</summary>
    Task SaveAsync(string code, string name, string bodyTr, string bodyEn, ActorContext actor, CancellationToken ct = default);
}

/// <summary>
/// Backs the admin settings-agents / settings-users template dialogs (ROADMAP §6.3 +
/// §4 B2). The mockup's rows are system content (osTicket keeps some of these in the
/// `content` page table); here they live as <see cref="EmailTemplate"/> rows in the
/// two seeded sets — one body per language, addressed by stable code names — so the
/// per-row Edit dialogs prefill from and save back to the same table the S3 canon
/// templates use (flagged for canon: page-content rows such as the sign-in banners
/// share the email-template table until a dedicated content mechanism exists).
/// Rows are created on first save (GetOrCreate), keeping pre-existing databases and
/// the seeded 21-template canon untouched.
/// </summary>
public sealed class SystemTemplateService(AppDbContext db) : ISystemTemplateService
{
    /// <summary>Catalog defaults (sample data stays Turkish/English per set — the
    /// welcome/guest bodies are the mockup dialogs' verbatim samples).</summary>
    private static readonly (string Code, string Name, string BodyTr, string BodyEn)[] Catalog =
    [
        // admin/settings-agents.html (sa.tpl*)
        ("staff.welcome", "Hoş Geldin E-postası",
            "Merhaba %{staff.name},\n\nRapidsolDestek temsilci hesabınız oluşturuldu. Giriş bilgilerinizi belirlemek için bağlantıya tıklayın: %{link}",
            "Hello %{staff.name},\n\nYour RapidsolDestek agent account has been created. Click the link to set your credentials: %{link}"),
        ("staff.banner", "Giriş Bandosu",
            "Planlı bakım: Cumartesi 02:00–04:00 arasında panel kısa süreli erişilemeyebilir.",
            "Planned maintenance: the panel may be briefly unavailable Saturday 02:00–04:00."),
        ("staff.pwreset", "Parola Sıfırlama",
            "Merhaba %{staff.name},\n\nParolanızı sıfırlamak için bağlantıya tıklayın: %{link}\n\nBağlantı 30 dakika geçerlidir.",
            "Hello %{staff.name},\n\nClick the link to reset your password: %{link}\n\nThe link is valid for 30 minutes."),
        ("staff.2fa", "2FA E-postası",
            "Merhaba %{staff.name},\n\nGiriş doğrulama kodunuz: %{code}",
            "Hello %{staff.name},\n\nYour sign-in verification code: %{code}"),

        // admin/settings-users.html (su.tpl*)
        ("user.access.link", "Misafir Erişim Bağlantısı",
            "Merhaba %{user.name},\n\nTalebinize erişmek için aşağıdaki bağlantıyı kullanabilirsiniz: %{link}",
            "Hello %{user.name},\n\nYou can use the link below to access your request: %{link}"),
        ("user.banner", "Giriş Sayfası",
            "RapidsolDestek portalına hoş geldiniz. Taleplerinizi görüntülemek için giriş yapın.",
            "Welcome to the RapidsolDestek portal. Sign in to view your requests."),
        ("user.pwreset", "Parola Sıfırlama",
            "Merhaba %{user.name},\n\nParolanızı sıfırlamak için bağlantıya tıklayın: %{link}",
            "Hello %{user.name},\n\nClick the link to reset your password: %{link}"),
        ("user.verify.page", "E-posta Doğrulama Sayfası",
            "E-posta adresiniz doğrulanıyor, lütfen bekleyin…",
            "Your email address is being verified, please wait…"),
        ("user.confirm.email", "Hesap Onay E-postası",
            "Merhaba %{user.name},\n\nHesabınızı doğrulamak için bağlantıya tıklayın: %{link}",
            "Hello %{user.name},\n\nClick the link to confirm your account: %{link}"),
        ("user.confirmed.page", "Hesap Onaylandı Sayfası",
            "Hesabınız doğrulandı. Artık giriş yapabilirsiniz.",
            "Your account is confirmed. You can sign in now."),
    ];

    public async Task<IReadOnlyList<SystemTemplateRow>> GetAsync(IReadOnlyList<string> codes, CancellationToken ct = default)
    {
        var stored = await db.Set<EmailTemplate>()
            .Where(t => codes.Contains(t.CodeName) && t.Set!.IsActive
                        && (t.Set!.Language == "tr" || t.Set!.Language == "en"))
            .Select(t => new { t.CodeName, t.Set!.Language, t.Subject, t.Body })
            .ToListAsync(ct);

        return [.. Catalog.Where(c => codes.Contains(c.Code)).Select(c =>
        {
            var tr = stored.FirstOrDefault(s => s.CodeName == c.Code && s.Language == "tr");
            var en = stored.FirstOrDefault(s => s.CodeName == c.Code && s.Language == "en");
            return new SystemTemplateRow(c.Code, tr?.Subject ?? en?.Subject ?? c.Name,
                tr?.Body ?? c.BodyTr, en?.Body ?? c.BodyEn);
        })];
    }

    public async Task SaveAsync(string code, string name, string bodyTr, string bodyEn, ActorContext actor, CancellationToken ct = default)
    {
        if (!Catalog.Any(c => c.Code == code))
            throw new DomainRuleException("template-unknown", $"'{code}' is not a system template code.");

        using (actor.BeginAuditScope())
        {
            await UpsertAsync(code, "tr", name, bodyTr, ct);
            await UpsertAsync(code, "en", name, bodyEn, ct);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task UpsertAsync(string code, string language, string name, string body, CancellationToken ct)
    {
        var row = await db.Set<EmailTemplate>()
            .FirstOrDefaultAsync(t => t.CodeName == code && t.Set!.IsActive && t.Set!.Language == language, ct);
        if (row is null)
        {
            var setId = await db.EmailTemplateSets
                .Where(s => s.IsActive && s.Language == language)
                .Select(s => (int?)s.Id).FirstOrDefaultAsync(ct)
                ?? throw new DomainRuleException("template-set-missing", $"No active '{language}' template set.");
            row = new EmailTemplate { SetId = setId, CodeName = code, Subject = name, Body = body };
            db.Set<EmailTemplate>().Add(row);
        }
        row.Subject = name;
        row.Body = body;
    }
}
