using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>Typed view over the effort namespace (B8 settings gates, seeded keys).</summary>
public sealed record EffortSettings(
    bool Enabled,
    bool BlockWorkUntilApproved,
    bool MandatoryRejectNote,
    int ReminderDays,
    decimal AutoApproveThresholdHours,
    int RevisionLimit);

/// <summary>Typed view over a numbering namespace ("tickets" / "tasks").</summary>
public sealed record NumberingSettings(string NumberFormat, int SequenceId, string? DefaultStatusKey);

/// <summary>
/// Typed view over the "attachments" namespace (admin/settings-system Ekler section, S7).
/// MaxSizeMb is LIVE — enforced at every upload ingress (portal open/reply, agent
/// composers, kb-faq). Storage is persisted-only until a second IFileStore backend
/// exists (only "fs" is registered; the mockup's "Veritabanı" default is sample state —
/// storing file contents in the database was deliberately dropped, see StoredFile).
/// AuthRequired is persisted-only: every download endpoint already sits behind auth;
/// the OFF state needs an anonymous KB route once a public KB exists (TODO, flagged).
/// </summary>
public sealed record AttachmentSettings(string Storage, int MaxSizeMb, bool AuthRequired)
{
    public long MaxSizeBytes => MaxSizeMb * 1024L * 1024L;
}

/// <summary>
/// Typed view over the "tickets" behavior namespace (admin/settings-tickets, S7).
/// Defaults follow osTicket where the engine consumes the key; mockup checked
/// states are sample data. Keys without an engine consumer yet are annotated at
/// their read site in <c>SettingsTicketsController</c>.
/// </summary>
public sealed record TicketBehaviorSettings(
    string LockMode,            // "disabled" | "view" | "activity" — TODO(S7+): consumed by the composer lock UI (IThreadService lock API exists, no composer wiring yet)
    int MaxOpenPerUser,         // 0 = unlimited; enforced in TicketService.CreateAsync for end-user creates
    bool Captcha,               // TODO(S9): consumed by guest/register rate limiting (§3 captcha row)
    bool ClaimOnResponse,       // enforced in ThreadService.PostAsync (staff response auto-claims)
    bool AutoReferOnClose,      // TODO(S7+): consumed by the referral mechanism (ticket-view Yönlendirmeler)
    bool RequireTopicToClose,   // enforced in TicketService.TransitionStatusAsync
    bool AllowExternalImages,   // TODO(S8): consumed by the email thread renderer
    bool CollabVisibility,      // TODO: portal collaborator scope — open decision #7.9 (ROADMAP)
    bool TopLevelCounts,        // enforced by the agent tickets queue-tree count badges
    int DefaultQueueId);        // 0 = built-in default; enforced by agent tickets initial queue

/// <summary>
/// Typed view over the "tasks" namespace beyond numbering (admin/settings-tasks, S7).
/// NumberMode is LIVE — TaskService.CreateAsync draws random unguessable numbers or
/// advances the seeded task sequence. DefaultPriorityKey is persisted-only:
/// TODO(S8): TaskItem carries no priority column yet (osTicket keeps task priority in
/// dynamic form data); consumed once the task form designer output lands.
/// </summary>
public sealed record TaskSettings(string NumberMode, string DefaultPriorityKey);

/// <summary>
/// Typed view over the "agents" namespace (admin/settings-agents.html, S7).
/// LIVE: AllowPwreset gates the staff pwreset flow (agent + admin routes),
/// ResetWindowMinutes owns the staff reset-link lifespan (StaffResetTokenProvider),
/// RequireTwofa forces an email-code second step for staff without an enrollment
/// (StaffSignInManager.IsTwoFactorEnabledAsync), and MaxLoginAttempts/LockoutMinutes
/// are the STAFF-WIDE lockout policy consumed at both sign-ins
/// (StaffAccountControllerBase.ApplyStaffLockoutAsync — the mockup's fields are
/// staff-wide; the earlier admin-only 3/30 tightening is resolved to the mockup's
/// 5/30 canon). Persisted-only (annotated at the controller map): NameFormat /
/// IdentityMasking / AvatarSource (TODO(S8): staff name/avatar rendering helpers),
/// BlockCollab (TODO: collaborator add flow — open decision #9 territory),
/// PasswordPolicy (TODO(S8): policy engine over the Identity statics; "basic" is
/// today's static floor), SessionTimeoutMinutes + IpBinding (TODO: B6 session work —
/// staff sessions are not tracked yet).
/// </summary>
public sealed record AgentSettings(
    string NameFormat,          // "full" | "lastfirst" | "short" | "username"
    bool IdentityMasking,
    string AvatarSource,        // "initials" | "gravatar"
    bool BlockCollab,
    string PasswordPolicy,      // "none" | "basic" | "strong"
    bool AllowPwreset,
    int ResetWindowMinutes,
    bool RequireTwofa,
    int MaxLoginAttempts,
    int LockoutMinutes,
    int SessionTimeoutMinutes,
    bool IpBinding);

/// <summary>
/// Typed view over the "users" namespace (admin/settings-users.html, S7).
/// LIVE: RegistrationMode gates the portal /register route and the login page's
/// register link ("public" self-serve; "invite"/"closed" refuse self-registration —
/// invited people join through /invite with a signed token instead, which is what
/// makes "invite" different from "closed"); MaxLoginAttempts/LockoutMinutes are the
/// customer lockout policy (portal AccountController.Login); AuthTokens (S8 slice 5)
/// puts a signed auto-login token on the ticket link in customer mail
/// (%{ticket.link} / %{recipient.ticket_link} — TicketMailHandler over
/// IMailLinkTokenService); EmailVerify (S8 slice 5) makes registration mail a
/// verification link built from the seeded user.confirm.email template and blocks
/// sign-in until it is followed (resend at /register/sent). Honest default for
/// EmailVerify stays false — the mockup's checked switch is sample state, and
/// accounts registered while it is off are confirmed on creation so switching it on
/// never locks anyone out. Persisted-only (annotated at the controller map):
/// NameFormat/AvatarSource (TODO(S8): user name/avatar rendering helpers),
/// RegistrationRequired (TODO(S8): guest ticket-open flow does not exist yet),
/// PasswordPolicy (TODO(S8): policy engine over Identity statics),
/// SessionTimeoutMinutes (TODO: B6 session work).
/// </summary>
public sealed record UserSettings(
    string NameFormat,          // "full" | "lastfirst" | "short"
    string AvatarSource,        // "initials" | "gravatar"
    bool RegistrationRequired,
    string RegistrationMode,    // "closed" | "public" | "invite"
    string PasswordPolicy,      // "none" | "basic" | "strong"
    int MaxLoginAttempts,
    int LockoutMinutes,
    int SessionTimeoutMinutes,
    bool AuthTokens,
    bool EmailVerify);

/// <summary>
/// Typed view over the "kb" namespace (admin/settings-kb, S7). EnableKb is the LIVE
/// master switch: portal /kb + /kb-article (+vote/attachment) return 404 and the
/// portal nav/home KB surfaces disappear while off. EnableCanned is LIVE: the agent
/// composers hide their canned-response menu and the canned insert endpoints refuse
/// canned ids. RequireLogin is persisted-only: the whole portal KB already sits
/// behind the PortalUser cookie; the OFF state needs an anonymous KB route
/// (settings-system auth_required twin — TODO, flagged on the ROADMAP row).
/// </summary>
public sealed record KbSettings(bool EnableKb, bool RequireLogin, bool EnableCanned);

/// <summary>
/// Typed view over the "features" namespace (admin/plugins.html, S7). §3 parity:
/// osTicket's PHP plugin runtime is REPLACED by feature flags over built-in
/// modules — each flag is true only while the module is both installed and
/// enabled (features/&lt;key&gt;.installed holds the install date, .enabled the
/// switch). LIVE consumers: AuthLdap gates the staff-edit LDAP auth-backend
/// option (StaffController); TwofaEmail is NOT stored here — it binds to the
/// existing agents/require_twofa key (StaffSignInManager email-code second step)
/// so the plugin row and settings-agents edit the SAME switch. Persisted-only
/// (annotated in PluginsController): StorageS3/StorageFs (only the "fs"
/// IFileStore backend is registered — attachments.storage twin), AuditLog (the
/// audit interceptor is a compliance floor and runs unconditionally; the mockup's
/// disabled row is sample state), SlackNotifications (TODO(S8): notification
/// fan-out does not exist yet).
/// </summary>
public sealed record FeatureSettings(
    bool AuthLdap,
    bool StorageS3,
    bool AuditLog,
    bool TwofaEmail,
    bool StorageFs,
    bool SlackNotifications);

/// <summary>
/// Typed view over the "email" namespace (admin/email-settings.html, S7).
/// LIVE: DefaultTemplateSetId — EmailTemplateRenderer (all catalog mails) renders
/// from this template set (0 = the active "tr" set, the pre-S7 behavior);
/// DefaultSmtp ("system" or an account id) + DefaultEmailAccountId — the S8
/// OutboundMailJob transport selection (explicit from-account → default_smtp →
/// "system" = default_email_id); AlertEmailAccountId + AdminEmail — S8 alert
/// fan-out from-account and admin alert recipient (Ticket/TaskMailHandler);
/// FetchEnabled + FetchAutoCron — the MailFetchJob master switches (S8 inbound);
/// StripQuoted + ReplySeparator — quoted-reply removal (QuotedReplyStripper),
/// UseEmailPriority — X-Priority/Importance → ticket priority, AcceptUnregistered —
/// unknown-sender gate, AutoAddCollabs — To/Cc → ThreadCollaborator (all consumed
/// by InboundMailProcessor); AttachmentsInEmail — the agent reply mail carries the
/// reply's own files (S8 slice 5, osTicket emailAttachments; inbound attachment
/// accept stays governed by the attachments/max_size_mb cap). PERSISTED-ONLY
/// (annotated per key): VerifyDomain (TODO(S8): MX lookup on address save needs a
/// DNS client). Referenced account ids also feed the emails-page delete guard.
/// </summary>
public sealed record EmailSettings(
    int DefaultTemplateSetId,   // 0 = active "tr" set
    int DefaultEmailAccountId,  // 0 = unset
    int AlertEmailAccountId,    // 0 = unset
    string? AdminEmail,
    bool VerifyDomain,
    bool FetchEnabled,
    bool FetchAutoCron,
    bool StripQuoted,
    string ReplySeparator,
    bool UseEmailPriority,
    bool AcceptUnregistered,
    bool AutoAddCollabs,
    string DefaultSmtp,         // "system" | EmailAccount id (with an SMTP channel)
    bool AttachmentsInEmail);

public interface ISettingsService
{
    Task<string?> GetAsync(string ns, string key, CancellationToken ct = default);
    Task SetAsync(string ns, string key, string value, CancellationToken ct = default);

    /// <summary>Whole namespace snapshot (admin settings pages render + save per section).</summary>
    Task<IReadOnlyDictionary<string, string>> GetSectionAsync(string ns, CancellationToken ct = default);

    Task<EffortSettings> GetEffortAsync(CancellationToken ct = default);
    Task<AttachmentSettings> GetAttachmentsAsync(CancellationToken ct = default);
    Task<NumberingSettings> GetTicketNumberingAsync(CancellationToken ct = default);
    Task<NumberingSettings> GetTaskNumberingAsync(CancellationToken ct = default);
    Task<TicketBehaviorSettings> GetTicketBehaviorAsync(CancellationToken ct = default);
    Task<TaskSettings> GetTasksAsync(CancellationToken ct = default);
    Task<KbSettings> GetKbAsync(CancellationToken ct = default);
    Task<AgentSettings> GetAgentsAsync(CancellationToken ct = default);
    Task<UserSettings> GetUsersAsync(CancellationToken ct = default);
    Task<EmailSettings> GetEmailAsync(CancellationToken ct = default);
    Task<FeatureSettings> GetFeaturesAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads/writes the osTicket-style config table (<see cref="Setting"/>). Values are
/// read fresh per scope (scoped lifetime + per-namespace memo) so tests and the S7
/// admin pages observe their own writes immediately.
/// </summary>
public sealed class SettingsService(AppDbContext db) : ISettingsService
{
    private readonly Dictionary<string, Dictionary<string, string>> _memo = [];

    public async Task<string?> GetAsync(string ns, string key, CancellationToken ct = default)
    {
        var section = await LoadAsync(ns, ct);
        return section.TryGetValue(key, out var value) ? value : null;
    }

    public async Task SetAsync(string ns, string key, string value, CancellationToken ct = default)
    {
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Namespace == ns && s.Key == key, ct);
        if (row is null)
        {
            row = new Setting { Namespace = ns, Key = key };
            db.Settings.Add(row);
        }
        row.Value = value;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _memo.Remove(ns);
    }

    public async Task<EffortSettings> GetEffortAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync("effort", ct);
        return new EffortSettings(
            Enabled: Bool(s, "enabled", true),
            BlockWorkUntilApproved: Bool(s, "block_work_until_approved", false),
            MandatoryRejectNote: Bool(s, "mandatory_reject_note", true),
            ReminderDays: Int(s, "reminder_days", 3),
            AutoApproveThresholdHours: Decimal(s, "auto_approve_threshold_hours", 0),
            RevisionLimit: Int(s, "revision_limit", 3));
    }

    public async Task<AttachmentSettings> GetAttachmentsAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync("attachments", ct);
        return new AttachmentSettings(
            // Honest default "fs": the only registered IFileStore backend (the mockup's
            // selected "Veritabanı" is sample state — flagged on the ROADMAP row).
            Storage: s.GetValueOrDefault("storage", "fs"),
            MaxSizeMb: Int(s, "max_size_mb", 16),
            AuthRequired: Bool(s, "auth_required", true));
    }

    public async Task<IReadOnlyDictionary<string, string>> GetSectionAsync(string ns, CancellationToken ct = default) =>
        await LoadAsync(ns, ct);

    public async Task<TicketBehaviorSettings> GetTicketBehaviorAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync("tickets", ct);
        return new TicketBehaviorSettings(
            LockMode: s.GetValueOrDefault("lock_mode", "activity"),
            MaxOpenPerUser: Int(s, "max_open_per_user", 0),
            Captcha: Bool(s, "captcha", true),
            ClaimOnResponse: Bool(s, "claim_on_response", true),
            AutoReferOnClose: Bool(s, "auto_refer_on_close", false),
            // osTicket parity: "require help topic to close" ships off; the mockup's
            // checked switch is sample state (flagged on the ROADMAP row).
            RequireTopicToClose: Bool(s, "require_topic_to_close", false),
            AllowExternalImages: Bool(s, "allow_external_images", false),
            CollabVisibility: Bool(s, "collab_visibility", true),
            TopLevelCounts: Bool(s, "top_level_counts", true),
            DefaultQueueId: Int(s, "default_queue_id", 0));
    }

    public async Task<TaskSettings> GetTasksAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync("tasks", ct);
        return new TaskSettings(
            // Seed truth: the task sequence is seeded and referenced, so sequential is
            // the honest default (the mockup's selected "Ardışık" agrees).
            NumberMode: s.GetValueOrDefault("number_mode") == "random" ? "random" : "sequential",
            DefaultPriorityKey: s.GetValueOrDefault("default_priority", "normal"));
    }

    public async Task<KbSettings> GetKbAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync("kb", ct);
        return new KbSettings(
            // The portal KB has shipped visible since S5, so enabled is the honest
            // default (matches the mockup's checked state; osTicket ships KB off —
            // deviation flagged on the ROADMAP row).
            EnableKb: Bool(s, "enable_kb", true),
            RequireLogin: Bool(s, "require_login", false),
            EnableCanned: Bool(s, "enable_canned", true));
    }

    public async Task<AgentSettings> GetAgentsAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync("agents", ct);
        return new AgentSettings(
            NameFormat: Choice(s, "name_format", ["full", "lastfirst", "short", "username"], "full"),
            IdentityMasking: Bool(s, "identity_masking", false),
            AvatarSource: Choice(s, "avatar_source", ["initials", "gravatar"], "initials"),
            BlockCollab: Bool(s, "block_collab", true),
            // Honest default "basic": the Identity statics enforce exactly the basic
            // floor today; the mockup's selected "strong" is sample state (flagged).
            PasswordPolicy: Choice(s, "password_policy", ["none", "basic", "strong"], "basic"),
            AllowPwreset: Bool(s, "allow_pwreset", true),
            ResetWindowMinutes: Int(s, "reset_window_minutes", 30),
            // Honest default false: agents have signed in without a second step since
            // S6; the mockup's checked switch is sample state (flagged).
            RequireTwofa: Bool(s, "require_twofa", false),
            // Staff-wide lockout canon = the mockup's selected 5 attempts / 30 minutes
            // (resolves the S7 admin-auth 3-vs-5 and 15-vs-30 flags toward the mockup).
            MaxLoginAttempts: Int(s, "max_login_attempts", 5),
            LockoutMinutes: Int(s, "lockout_minutes", 30),
            SessionTimeoutMinutes: Int(s, "session_timeout_minutes", 120),
            IpBinding: Bool(s, "ip_binding", false));
    }

    public async Task<UserSettings> GetUsersAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync("users", ct);
        return new UserSettings(
            NameFormat: Choice(s, "name_format", ["full", "lastfirst", "short"], "full"),
            AvatarSource: Choice(s, "avatar_source", ["initials", "gravatar"], "initials"),
            RegistrationRequired: Bool(s, "registration_required", true),
            // "public" is both the mockup's selected option and today's behavior
            // (the S5 register flow has always been open).
            RegistrationMode: Choice(s, "registration_mode", ["closed", "public", "invite"], "public"),
            PasswordPolicy: Choice(s, "password_policy", ["none", "basic", "strong"], "basic"),
            MaxLoginAttempts: Int(s, "max_login_attempts", 5),
            LockoutMinutes: Int(s, "lockout_minutes", 30),
            SessionTimeoutMinutes: Int(s, "session_timeout_minutes", 0),
            AuthTokens: Bool(s, "auth_tokens", true),
            // Honest default false: registration signs the account in immediately
            // today — the mockup's checked switch is sample state (flagged).
            EmailVerify: Bool(s, "email_verify", false));
    }

    public async Task<EmailSettings> GetEmailAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync("email", ct);
        return new EmailSettings(
            // 0 = the active "tr" set: exactly what the effort emails used before S7,
            // so the honest default (the mockup's selected "Varsayılan (TR)" agrees).
            DefaultTemplateSetId: Int(s, "default_template_set_id", 0),
            // Honest default 0/unset: the S8 queue then has no "system" account and
            // dev-falls-back/fails typed — the mockup's selected destek@/bilgi@ rows
            // are sample state (flagged).
            DefaultEmailAccountId: Int(s, "default_email_id", 0),
            AlertEmailAccountId: Int(s, "alert_email_id", 0),
            AdminEmail: s.GetValueOrDefault("admin_email"),
            VerifyDomain: Bool(s, "verify_domain", true),
            FetchEnabled: Bool(s, "fetch_enabled", true),
            FetchAutoCron: Bool(s, "fetch_auto_cron", true),
            StripQuoted: Bool(s, "strip_quoted", true),
            // The mockup's sample separator doubles as the default (osTicket ships an
            // English marker; the TR product text is the canon here).
            ReplySeparator: s.GetValueOrDefault("reply_separator", "-- lütfen bu satırın üstüne yazın --"),
            UseEmailPriority: Bool(s, "use_email_priority", false),
            AcceptUnregistered: Bool(s, "accept_unregistered", true),
            AutoAddCollabs: Bool(s, "auto_add_collabs", true),
            // LIVE (S8 OutboundMailJob): "system" = default_email_id, else an
            // account id — the mockup's selected "destek@… — SMTP" is sample state.
            DefaultSmtp: s.GetValueOrDefault("default_smtp", "system"),
            AttachmentsInEmail: Bool(s, "attachments_in_email", true));
    }

    public async Task<FeatureSettings> GetFeaturesAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync("features", ct);
        // A module is ON while installed AND enabled. Defaults mirror the seeded
        // plugins.html canon (auth-ldap + storage-s3 installed/active, audit
        // installed/disabled, the rest uninstalled) so a missing row keeps today's
        // behavior — e.g. the staff-edit LDAP option stays offered on a fresh db.
        bool On(string key, bool fallback) =>
            Installed(s, key, fallback) && Bool(s, $"{key}.enabled", fallback);
        return new FeatureSettings(
            AuthLdap: On("auth_ldap", true),
            StorageS3: On("storage_s3", true),
            AuditLog: Installed(s, "audit", true) && Bool(s, "audit.enabled", false),
            // Shared key: the 2FA-email plugin row IS agents/require_twofa
            // (StaffSignInManager consumer) — no features/* duplicate is stored.
            TwofaEmail: (await GetAgentsAsync(ct)).RequireTwofa,
            StorageFs: On("storage_fs", false),
            SlackNotifications: On("slack", false));
    }

    /// <summary>features/&lt;key&gt;.installed stores the install DATE (ISO) —
    /// any non-empty value counts as installed (admin/plugins "Kurulma" column).</summary>
    private static bool Installed(Dictionary<string, string> s, string key, bool fallback) =>
        s.TryGetValue($"{key}.installed", out var v) ? v.Length > 0 : fallback;

    public Task<NumberingSettings> GetTicketNumberingAsync(CancellationToken ct = default) =>
        GetNumberingAsync("tickets", "R######", ct);

    public Task<NumberingSettings> GetTaskNumberingAsync(CancellationToken ct = default) =>
        GetNumberingAsync("tasks", "T-####", ct);

    private async Task<NumberingSettings> GetNumberingAsync(string ns, string fallbackFormat, CancellationToken ct)
    {
        var s = await LoadAsync(ns, ct);
        return new NumberingSettings(
            NumberFormat: s.GetValueOrDefault("number_format", fallbackFormat),
            SequenceId: Int(s, "sequence_id", 0),
            DefaultStatusKey: s.GetValueOrDefault("default_status"));
    }

    private async Task<Dictionary<string, string>> LoadAsync(string ns, CancellationToken ct)
    {
        if (_memo.TryGetValue(ns, out var cached))
            return cached;

        var section = await db.Settings.Where(s => s.Namespace == ns)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        _memo[ns] = section;
        return section;
    }

    private static string Choice(Dictionary<string, string> s, string key, string[] allowed, string fallback) =>
        s.TryGetValue(key, out var v) && allowed.Contains(v) ? v : fallback;

    private static bool Bool(Dictionary<string, string> s, string key, bool fallback) =>
        s.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    private static int Int(Dictionary<string, string> s, string key, int fallback) =>
        s.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;

    private static decimal Decimal(Dictionary<string, string> s, string key, decimal fallback) =>
        s.TryGetValue(key, out var v) && decimal.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
}
