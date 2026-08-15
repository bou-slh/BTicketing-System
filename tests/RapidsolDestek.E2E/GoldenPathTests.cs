using Microsoft.Playwright;

namespace RapidsolDestek.E2E;

/// <summary>
/// S6 exit gate (ROADMAP §5): the full golden path across both panels —
/// portal user opens a ticket → agent proposes a 6h effort → portal user approves
/// → agent replies and resolves. Runs against a RUNNING app (E2E_BASE_URL, default
/// http://localhost:5310). Creates its own fresh user+ticket, so seeded canon
/// (hero R716555) is untouched; the DB still gains rows — reseed for a pristine demo.
/// Agent = saydin (Bordro dept — the Bordro help topic routes there; non-admin, no TOTP).
/// </summary>
[Trait("Category", "E2E")]
public class GoldenPathTests : IAsyncLifetime
{
    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://localhost:5310";

    private static readonly string SeedPassword =
        Environment.GetEnvironmentVariable("SEED_PASSWORD") ?? "rapidsol1dev";

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
    public async Task GoldenPath_PortalOpens_AgentProposes_PortalApproves_AgentResolves()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var subject = $"E2E altın yol {unique}";

        // Separate cookie universes for the two panels.
        var portal = await (await _browser.NewContextAsync()).NewPageAsync();
        var agent = await (await _browser.NewContextAsync()).NewPageAsync();

        // -- 1. Portal: fresh user registers and opens a Bordro ticket --------
        await portal.GotoAsync($"{BaseUrl}/register");
        await portal.FillAsync("#rg-email", $"e2e.gp.{unique}@ulasim.com.tr");
        await portal.FillAsync("#rg-name", $"E2E GoldenPath {unique}");
        await portal.FillAsync("#rg-pass", SeedPassword);
        await portal.FillAsync("#rg-pass2", SeedPassword);
        await portal.CheckAsync("label.rd-check input[type=checkbox]");
        await portal.ClickAsync("form button.rc-button-primary");
        await portal.WaitForURLAsync($"{BaseUrl}/");

        await portal.GotoAsync($"{BaseUrl}/open");
        await portal.SelectOptionAsync("#rc-topic", new SelectOptionValue { Label = "Bordro" });
        await portal.FillAsync("#rc-summary", subject);
        await portal.FillAsync("#rc-details", $"Altın yol E2E {unique}: bordro dökümü hatalı görünüyor.");
        await portal.ClickAsync(".rc-form-footer button.rc-button-primary");
        await portal.WaitForURLAsync($"{BaseUrl}/tickets**");

        await portal.ClickAsync($"a.rc-ticket-row:has-text('{subject}')");
        await portal.WaitForURLAsync($"{BaseUrl}/ticket-view**");
        var ticketId = new Uri(portal.Url).Query.Split("id=")[1].Split('&')[0];
        Assert.False(string.IsNullOrEmpty(ticketId));

        // -- 2. Agent (saydin, Bordro): proposes a 6h effort ------------------
        await agent.GotoAsync($"{BaseUrl}/agent/login");
        await agent.FillAsync("#lg-user", "saydin");
        await agent.FillAsync("#lg-pass", SeedPassword);
        await agent.ClickAsync("form button[type=submit], form button.rc-button-primary");
        await agent.WaitForURLAsync($"{BaseUrl}/agent/**");

        await agent.GotoAsync($"{BaseUrl}/agent/ticket-view?id={ticketId}");
        await Assertions.Expect(agent.Locator("body")).ToContainTextAsync(subject);
        await agent.ClickAsync("[data-dialog-open='dlg-effort']");
        await agent.FillAsync("#ef-hours", "6");
        await agent.FillAsync("#ef-note", $"Tahmini analiz + düzeltme ({unique})");
        await agent.ClickAsync("#dlg-effort .rd-btn-primary");
        await agent.WaitForURLAsync($"{BaseUrl}/agent/ticket-view**");
        // Pending card: withdraw/revise controls present.
        await Assertions.Expect(agent.Locator(".bo-toggle-card")).ToBeVisibleAsync();

        // -- 3. Portal: owner sees the effort card and approves ---------------
        await portal.GotoAsync($"{BaseUrl}/ticket-view?id={ticketId}");
        var card = portal.Locator(".rc-approval");
        await Assertions.Expect(card).ToBeVisibleAsync();
        await portal.ClickAsync(".rc-approval-actions form button.rc-button-primary");
        await portal.WaitForURLAsync($"{BaseUrl}/ticket-view**");
        await Assertions.Expect(portal.Locator(".rc-approval .rc-approval-icon")).ToHaveTextAsync("✓");

        // -- 4. Agent: sees the approved banner, replies and resolves ---------
        await agent.GotoAsync($"{BaseUrl}/agent/ticket-view?id={ticketId}");
        await Assertions.Expect(agent.Locator(".rd-banner-success")).ToBeVisibleAsync();

        await agent.FillAsync(".rd-tab-panel.active textarea[name=body]",
            $"Düzeltme tamamlandı ({unique}), kapatıyorum.");
        await agent.SelectOptionAsync(".rd-tab-panel.active select[name=statusId]",
            new SelectOptionValue { Label = "Çözüldü" });
        await agent.ClickAsync(".rd-tab-panel.active button.rd-btn-primary");
        await agent.WaitForURLAsync($"{BaseUrl}/agent/ticket-view**");

        // Resolved: status pill in the header meta shows Çözüldü, approval banner intact.
        await Assertions.Expect(agent.Locator(".rd-banner-success")).ToBeVisibleAsync();
        await Assertions.Expect(agent.GetByText("Çözüldü").First).ToBeVisibleAsync();

        // Portal side reflects the resolution.
        await portal.GotoAsync($"{BaseUrl}/ticket-view?id={ticketId}");
        await Assertions.Expect(portal.GetByText("Çözüldü").First).ToBeVisibleAsync();
    }
}
