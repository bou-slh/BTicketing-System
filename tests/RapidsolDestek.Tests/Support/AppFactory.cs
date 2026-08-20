using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Tests.Support;

/// <summary>
/// WebApplicationFactory over the real Program, pointed at the test container and
/// with outbound mail captured instead of sent. Reused by service-level tests
/// (resolve services from <see cref="WebApplicationFactory{T}.Services"/>) and by
/// S5+ page tests (use <see cref="WebApplicationFactory{T}.CreateClient()"/>).
/// S8: the mail queue is live — no Hangfire server runs here; the stubbed
/// <see cref="IBackgroundJobClient"/> (<see cref="InlineJobClient"/>) executes the
/// send job inline, and the SMTP transport seam + the no-SMTP dev fallback are both
/// captured into <see cref="Emails"/> so queued sends stay observable.
/// </summary>
public sealed class AppFactory(string connectionString) : WebApplicationFactory<Program>
{
    public CapturingEmailSender Emails { get; } = new();

    /// <summary>Queued sends that reached the SMTP transport seam (from-account visible).</summary>
    public CapturingSmtpTransport Transport { get; private set; } = null!;

    /// <summary>The inline job stub — assert scheduled jobs via <see cref="InlineJobClient.Created"/>.</summary>
    public InlineJobClient Jobs => Services.GetRequiredService<InlineJobClient>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Hangfire:ServerEnabled", "false"); // jobs run inline via the stub
        Transport = new CapturingSmtpTransport(Emails);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IAppEmailSender>(Emails);
            // The S7 email-diagnostic transport is a REAL MailKit SMTP submit —
            // tests capture it into the same fake instead (channel-config failures
            // still occur for real: they are typed BEFORE the transport runs).
            services.AddSingleton<RapidsolDestek.Infrastructure.Services.IMailDiagnosticSender>(
                new CapturingDiagnosticSender(Emails));
            // S8 queue seams: capture at the SMTP transport, capture the dev
            // fallback (no usable SMTP account), execute scheduled jobs inline.
            services.AddSingleton<ISmtpMailTransport>(Transport);
            services.AddSingleton<IMailFallbackSender>(new CapturingFallbackSender(Emails));
            services.AddSingleton<InlineJobClient>();
            services.AddSingleton<IBackgroundJobClient>(sp => sp.GetRequiredService<InlineJobClient>());
        });
    }
}

/// <summary>Forwards diagnostic sends into <see cref="CapturingEmailSender"/> and
/// reports the transport success stage.</summary>
public sealed class CapturingDiagnosticSender(CapturingEmailSender emails)
    : RapidsolDestek.Infrastructure.Services.IMailDiagnosticSender
{
    public async Task<RapidsolDestek.Infrastructure.Services.MailTestResult> SendAsync(
        RapidsolDestek.Infrastructure.Services.MailSendRequest request, CancellationToken ct = default)
    {
        await emails.SendAsync(request.To, request.Subject, request.TextBody, ct);
        return new RapidsolDestek.Infrastructure.Services.MailTestResult(true, "sent");
    }
}

/// <summary>Records every outbound mail so tests can assert on template sends.</summary>
public sealed class CapturingEmailSender : IAppEmailSender
{
    private readonly List<CapturedEmail> _sent = [];

    public IReadOnlyList<CapturedEmail> Sent
    {
        get { lock (_sent) return [.. _sent]; }
    }

    public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        lock (_sent)
            _sent.Add(new CapturedEmail(to, subject, htmlBody, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public void Clear()
    {
        lock (_sent) _sent.Clear();
    }
}

public sealed record CapturedEmail(string To, string Subject, string HtmlBody, DateTimeOffset SentAt);

/// <summary>One send as it crossed the S8 SMTP transport seam (queue → job → SMTP).</summary>
public sealed record CapturedSmtpSend(
    string FromAddress, string? FromName, string To, string? Cc, string Subject, string HtmlBody);

/// <summary>
/// S8 SMTP transport fake: successful sends land in <see cref="Sent"/> AND in the
/// shared <see cref="CapturingEmailSender"/> (so pre-S8 assertions keep working).
/// Queue failure results with <see cref="ScriptNextResult"/> to exercise the outbox
/// state machine; unscripted calls succeed.
/// </summary>
public sealed class CapturingSmtpTransport(CapturingEmailSender emails) : ISmtpMailTransport
{
    private readonly List<CapturedSmtpSend> _sent = [];
    private readonly Queue<RapidsolDestek.Infrastructure.Services.MailTestResult> _script = new();

    public IReadOnlyList<CapturedSmtpSend> Sent
    {
        get { lock (_sent) return [.. _sent]; }
    }

    public void ScriptNextResult(RapidsolDestek.Infrastructure.Services.MailTestResult result)
    {
        lock (_sent) _script.Enqueue(result);
    }

    public void Clear()
    {
        lock (_sent)
        {
            _sent.Clear();
            _script.Clear();
        }
    }

    public async Task<RapidsolDestek.Infrastructure.Services.MailTestResult> SendAsync(
        SmtpTransportSettings smtp, OutboundSmtpMessage message, CancellationToken ct = default)
    {
        lock (_sent)
        {
            if (_script.TryDequeue(out var scripted) && !scripted.Success)
                return scripted;
            _sent.Add(new CapturedSmtpSend(message.FromAddress, message.FromName,
                message.To, message.Cc, message.Subject, message.HtmlBody));
        }
        await emails.SendAsync(message.To, message.Subject, message.HtmlBody, ct);
        return new RapidsolDestek.Infrastructure.Services.MailTestResult(true, "sent");
    }
}

/// <summary>The queue's no-usable-SMTP fallback, captured (AppFactory runs as
/// Development, matching the real dev-logging fallback registration).</summary>
public sealed class CapturingFallbackSender(CapturingEmailSender emails) : IMailFallbackSender
{
    public Task SendAsync(string to, string? cc, string subject, string htmlBody, CancellationToken ct = default) =>
        emails.SendAsync(to, subject, htmlBody, ct);
}

/// <summary>
/// IBackgroundJobClient stub: records every scheduled job and executes enqueued ones
/// INLINE (fresh scope, like a Hangfire worker) so queued sends complete before the
/// triggering call returns. Exceptions are swallowed — fire-and-forget parity with a
/// real server; retry behavior is tested by invoking the job method directly.
/// </summary>
public sealed class InlineJobClient(IServiceProvider services) : IBackgroundJobClient
{
    private readonly List<(Type Type, string Method, IReadOnlyList<object?> Args)> _created = [];

    public IReadOnlyList<(Type Type, string Method, IReadOnlyList<object?> Args)> Created
    {
        get { lock (_created) return [.. _created]; }
    }

    public string Create(Job job, IState state)
    {
        lock (_created)
            _created.Add((job.Type, job.Method.Name, [.. job.Args]));

        if (state is EnqueuedState)
        {
            try
            {
                using var scope = services.CreateScope();
                var instance = ActivatorUtilities.GetServiceOrCreateInstance(scope.ServiceProvider, job.Type);
                var result = job.Method.Invoke(instance, [.. job.Args]);
                (result as Task)?.GetAwaiter().GetResult();
            }
            catch
            {
                // Job failures never surface to the enqueuer (server parity); the
                // outbox row carries the honest state for assertions.
            }
        }
        return Guid.NewGuid().ToString("n");
    }

    public bool ChangeState(string jobId, IState state, string expectedState) => true;
}
