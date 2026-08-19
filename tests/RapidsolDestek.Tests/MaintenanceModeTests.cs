using Microsoft.AspNetCore.Http;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web;

namespace RapidsolDestek.Tests;

/// <summary>
/// Maintenance-mode gating (portal/offline.html row, ROADMAP §6.1): the system/offline
/// switch blocks the client portal only — staff spaces, the offline page itself,
/// the culture endpoint and static assets stay reachable. No database needed.
/// </summary>
public class MaintenanceModeTests
{
    private sealed class FakeSettings(string? offline) : ISettingsService
    {
        public Task<string?> GetAsync(string ns, string key, CancellationToken ct = default) =>
            Task.FromResult(ns == MaintenanceModeMiddleware.SettingNamespace &&
                            key == MaintenanceModeMiddleware.SettingKey ? offline : null);

        public Task SetAsync(string ns, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, string>> GetSectionAsync(string ns, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EffortSettings> GetEffortAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AttachmentSettings> GetAttachmentsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NumberingSettings> GetTicketNumberingAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NumberingSettings> GetTaskNumberingAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TicketBehaviorSettings> GetTicketBehaviorAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TaskSettings> GetTasksAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KbSettings> GetKbAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static async Task<HttpContext> RunAsync(string path, string? offline, string method = "GET")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        var middleware = new MaintenanceModeMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(ctx, new FakeSettings(offline));
        return ctx;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/register")]
    [InlineData("/pwreset")]
    [InlineData("/tickets")]
    [InlineData("/open")]
    public async Task PortalRoutes_ServeOfflinePage_WhenOn(string path)
    {
        var ctx = await RunAsync(path, "true");
        Assert.Equal("/offline", ctx.Request.Path.Value);
        Assert.Equal("GET", ctx.Request.Method);
    }

    [Fact]
    public async Task PortalMutations_AreSwallowedIntoGetOffline_WhenOn()
    {
        var ctx = await RunAsync("/open", "true", method: "POST");
        Assert.Equal("/offline", ctx.Request.Path.Value);
        Assert.Equal("GET", ctx.Request.Method);
    }

    [Theory]
    [InlineData("/agent/login")]
    [InlineData("/agent/tickets")]
    [InlineData("/admin/login")]
    [InlineData("/admin/dashboard")]
    [InlineData("/offline")]
    [InlineData("/culture")]
    [InlineData("/css/portal.css")]
    [InlineData("/js/rd.js")]
    [InlineData("/favicon.svg")]
    public async Task StaffRoutesAndAssets_StayReachable_WhenOn(string path)
    {
        var ctx = await RunAsync(path, "true");
        Assert.Equal(path, ctx.Request.Path.Value);
    }

    [Theory]
    [InlineData("false")] // seeded default
    [InlineData(null)]    // missing row counts as off
    [InlineData("junk")]  // unparsable counts as off
    public async Task PortalRoutes_Untouched_WhenOff(string? value)
    {
        var ctx = await RunAsync("/tickets", value);
        Assert.Equal("/tickets", ctx.Request.Path.Value);
    }
}
