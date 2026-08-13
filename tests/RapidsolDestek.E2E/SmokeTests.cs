using Microsoft.Playwright;

namespace RapidsolDestek.E2E;

/// <summary>
/// Browser smoke against a RUNNING app (default http://localhost:5310; override with
/// E2E_BASE_URL). Start the stack first: docker compose up -d db + dotnet run
/// (see /run skill). Filtered out of plain `dotnet test` runs via the E2E trait —
/// CI's main job runs --filter Category!=E2E; the e2e job boots the stack and
/// runs --filter Category=E2E after `playwright.ps1 install chromium`.
/// </summary>
[Trait("Category", "E2E")]
public class SmokeTests : IAsyncLifetime
{
    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://localhost:5310";

    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    [Fact]
    public async Task AgentLogin_WithSeededAgent_ReachesDashboard()
    {
        var page = await _browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/agent/login");

        await page.FillAsync("#lg-user", "dkaya");
        await page.FillAsync("#lg-pass", Environment.GetEnvironmentVariable("SEED_PASSWORD") ?? "rapidsol1dev");
        await page.ClickAsync("button[type=submit]");

        await page.WaitForURLAsync($"{BaseUrl}/agent/**");
        Assert.DoesNotContain("/login", page.Url);

        // Backoffice chrome rendered for the agent panel.
        var panel = await page.GetAttributeAsync("body", "data-panel");
        Assert.Equal("agent", panel);
    }

    [Fact]
    public async Task PortalLogin_PageRenders_InTurkishByDefault()
    {
        var page = await _browser.NewPageAsync();
        var response = await page.GotoAsync($"{BaseUrl}/login");

        Assert.NotNull(response);
        Assert.True(response!.Ok, $"GET /login returned {response.Status}");
        Assert.Equal("tr", await page.GetAttributeAsync("html", "lang"));
        Assert.True(await page.Locator("#lg-user, input[autocomplete=username]").First.IsVisibleAsync());
    }
}
