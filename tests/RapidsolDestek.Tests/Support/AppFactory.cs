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
        });
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
