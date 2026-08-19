using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Services;

namespace RapidsolDestek.Web.Services;

/// <summary>One diagnostic send's observable state (admin/email-diagnostic, B10):
/// "pending" → "success" | "failure". <see cref="Stage"/> is the typed reason key
/// on failure ("account"/"channel"/"input"/"dns"/"connect"/"tls"/"auth"/"send"/
/// "error"); <see cref="Detail"/> is the transport's raw message, if any.</summary>
public sealed record DiagnosticJob(string State, string? Stage, string? Detail)
{
    public static readonly DiagnosticJob Pending = new("pending", null, null);
}

public interface IEmailDiagnosticService
{
    /// <summary>Starts one background test-send; the returned id polls
    /// <see cref="Get"/> until the state turns terminal.</summary>
    Guid Start(int accountId, string to, string subject, string message);

    DiagnosticJob? Get(Guid id);
}

/// <summary>
/// admin/email-diagnostic engine (ROADMAP §6.3 + §4 B10): a REAL send through the
/// chosen account's SMTP channel (<see cref="IMailDiagnosticSender"/> — live
/// MailKit), tracked in memory so the page can render pending and poll to the
/// honest terminal state. Channel misconfiguration (no active SMTP channel, no
/// host) fails typed BEFORE any network I/O; transport failures carry the MailKit
/// stage. Jobs are pruned after an hour — the diagnostic is ephemeral by design
/// (no mail-log table exists yet; S8's pipeline adds durable logging).
/// </summary>
public sealed class EmailDiagnosticService(
    IServiceScopeFactory scopes,
    IMailDiagnosticSender sender,
    IEmailSecretProtector secrets,
    ILogger<EmailDiagnosticService> logger) : IEmailDiagnosticService
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, (DiagnosticJob Job, DateTimeOffset CreatedAt)> _jobs = new();

    public Guid Start(int accountId, string to, string subject, string message)
    {
        foreach (var (key, value) in _jobs)
        {
            if (value.CreatedAt < DateTimeOffset.UtcNow - Retention)
                _jobs.TryRemove(key, out _);
        }

        var id = Guid.NewGuid();
        _jobs[id] = (DiagnosticJob.Pending, DateTimeOffset.UtcNow);
        _ = Task.Run(() => RunAsync(id, accountId, to, subject, message));
        return id;
    }

    public DiagnosticJob? Get(Guid id) => _jobs.TryGetValue(id, out var entry) ? entry.Job : null;

    private async Task RunAsync(Guid id, int accountId, string to, string subject, string message)
    {
        try
        {
            EmailAccount? account;
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                account = await db.EmailAccounts.Include(a => a.Channels)
                    .AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId);
            }
            if (account is null)
            {
                Finish(id, "failure", "account", null);
                return;
            }

            // "That address's SMTP settings are used" (ed.fromHelp): an account
            // without a usable outgoing channel is an honest typed failure.
            var smtp = account.Channels.FirstOrDefault(c => c.Kind == EmailChannelKind.Smtp);
            if (smtp is null || !smtp.IsActive || string.IsNullOrWhiteSpace(smtp.Host) || smtp.Port is < 1 or > 65535)
            {
                Finish(id, "failure", "channel", null);
                return;
            }

            var result = await sender.SendAsync(new MailSendRequest(
                smtp.Host, smtp.Port, smtp.AuthKind, smtp.Username,
                secrets.Unprotect(smtp.PasswordProtected),
                account.Address, account.DisplayName, to, subject, message));
            Finish(id, result.Success ? "success" : "failure", result.Success ? null : result.Stage, result.Detail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Email diagnostic job {Id} crashed", id);
            Finish(id, "failure", "error", ex.Message);
        }
    }

    private void Finish(Guid id, string state, string? stage, string? detail) =>
        _jobs[id] = (new DiagnosticJob(state, stage, detail), DateTimeOffset.UtcNow);
}
