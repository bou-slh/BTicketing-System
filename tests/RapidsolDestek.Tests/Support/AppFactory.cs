using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Tests.Support;

/// <summary>
/// WebApplicationFactory over the real Program, pointed at the test container and
/// with outbound mail captured instead of sent. Reused by service-level tests
/// (resolve services from <see cref="WebApplicationFactory{T}.Services"/>) and by
/// S5+ page tests (use <see cref="WebApplicationFactory{T}.CreateClient()"/>).
/// </summary>
public sealed class AppFactory(string connectionString) : WebApplicationFactory<Program>
{
    public CapturingEmailSender Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IAppEmailSender>(Emails);
            // The S7 email-diagnostic transport is a REAL MailKit SMTP submit —
            // tests capture it into the same fake instead (channel-config failures
            // still occur for real: they are typed BEFORE the transport runs).
            services.AddSingleton<RapidsolDestek.Infrastructure.Services.IMailDiagnosticSender>(
                new CapturingDiagnosticSender(Emails));
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
