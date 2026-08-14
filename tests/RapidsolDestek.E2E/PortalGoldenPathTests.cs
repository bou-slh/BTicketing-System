using Microsoft.Playwright;

namespace RapidsolDestek.E2E;

/// <summary>
/// S5 exit gate (ROADMAP §5): portal golden path — register → open → reply → effort.
/// Runs against a RUNNING app like SmokeTests (E2E_BASE_URL, default http://localhost:5310).
/// The effort leg decides the seeded pending proposal on hero ticket R716555 as its owner
/// (agent-side proposal UI is S6), so it MUTATES seeded canon — drop/reseed the dev db
/// afterwards; CI's e2e job boots a fresh stack every run.
/// </summary>
[Trait("Category", "E2E")]
public class PortalGoldenPathTests : IAsyncLifetime
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
    public async Task Register_Open_Reply_FreshUser_CompletesGoldenPath()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var subject = $"E2E yol ücreti sorusu {unique}";
        var replyBody = $"E2E ek bilgi {unique}";
        var page = await _browser.NewPageAsync();

        // -- Register (KVKK consent required since the S5 auth port) ----------
        await page.GotoAsync($"{BaseUrl}/register");
        await page.FillAsync("#rg-email", $"e2e.{unique}@ulasim.com.tr");
        await page.FillAsync("#rg-name", $"E2E Kullanıcı {unique}");
        await page.FillAsync("#rg-pass", SeedPassword);
        await page.FillAsync("#rg-pass2", SeedPassword);
        await page.CheckAsync("label.rd-check input[type=checkbox]");
        await page.ClickAsync("form button.rc-button-primary");
        await page.WaitForURLAsync($"{BaseUrl}/");
        Assert.Equal("portal", await page.GetAttributeAsync("body", "data-panel"));

        // -- Open a ticket (Bordro topic: no extra designed fields) -----------
        await page.GotoAsync($"{BaseUrl}/open");
        await page.SelectOptionAsync("#rc-topic", new SelectOptionValue { Label = "Bordro" });
        await page.FillAsync("#rc-summary", subject);
        await page.FillAsync("#rc-details", $"Otomatik E2E kaydı {unique}. Bordro dökümünde soru.");
        await page.ClickAsync(".rc-form-footer button.rc-button-primary");
        await page.WaitForURLAsync($"{BaseUrl}/tickets**");

        var row = page.Locator("a.rc-ticket-row", new() { HasText = subject });
        await Assertions.Expect(row).ToBeVisibleAsync();
        // Registration auto-linked the @ulasim.com.tr address to the seeded org
        // (Organization.Domain match) — the row meta carries the org name.
        await Assertions.Expect(row.Locator(".rc-ticket-meta")).ToContainTextAsync("Ulaşım A.Ş.");

        // -- Reply on the new ticket ------------------------------------------
        await row.ClickAsync();
        await page.WaitForURLAsync($"{BaseUrl}/ticket-view**");
        await Assertions.Expect(page.Locator(".rc-message", new() { HasText = subject.Split(' ')[0] }).First)
            .ToBeVisibleAsync();

        await page.FillAsync("#rc-reply", replyBody);
        await page.ClickAsync(".rc-reply button.rc-button-primary");
        await page.WaitForURLAsync($"{BaseUrl}/ticket-view**");
        await Assertions.Expect(page.Locator(".rc-message", new() { HasText = replyBody }))
            .ToBeVisibleAsync();
    }

    [Fact]
    public async Task Effort_HeroTicketPendingProposal_OwnerApproves()
    {
        var page = await _browser.NewPageAsync();

        // -- Login as the seeded hero-ticket owner ----------------------------
        await page.GotoAsync($"{BaseUrl}/login");
        await page.FillAsync("#lg-user", "bourla.salehi@ulasim.com.tr");
        await page.FillAsync("#lg-pass", SeedPassword);
        await page.ClickAsync("form button[type=submit], form button.rc-button-primary");
        await page.WaitForURLAsync($"{BaseUrl}/tickets**");

        // -- Hero ticket R716555 carries the seeded 6h pending proposal -------
        await page.ClickAsync("a.rc-ticket-row:has-text('R716555')");
        await page.WaitForURLAsync($"{BaseUrl}/ticket-view**");

        var card = page.Locator(".rc-approval");
        await Assertions.Expect(card).ToBeVisibleAsync();
        await Assertions.Expect(card.Locator(".rc-approval-actions")).ToBeVisibleAsync();

        // -- Reddet requires a note (B8): empty dialog submit is blocked ------
        // (The full reject transition is service-tested in S4; the portal reject
        // E2E lands with the S6 golden path once agents can propose via UI —
        // this seeded proposal is the only pending one and approve consumes it.)
        await page.ClickAsync("[data-dialog-open='dlg-effort-reject']");
        var note = page.Locator("#rc-reject-note");
        await Assertions.Expect(note).ToBeVisibleAsync();
        await page.ClickAsync("#dlg-effort-reject form button.rc-button-primary, #dlg-effort-reject button[type=submit]");
        Assert.False(await note.EvaluateAsync<bool>("el => el.checkValidity()"));
        await Assertions.Expect(card.Locator(".rc-approval-actions")).ToBeVisibleAsync(); // still undecided
        await page.ClickAsync("#dlg-effort-reject .rd-dialog-foot [data-dialog-close]");

        // -- Onayla (one click, B8) → decided card, actions gone --------------
        await page.ClickAsync(".rc-approval-actions form button.rc-button-primary");
        await page.WaitForURLAsync($"{BaseUrl}/ticket-view**");

        await Assertions.Expect(page.Locator(".rc-approval .rc-approval-icon")).ToHaveTextAsync("✓");
        await Assertions.Expect(page.Locator(".rc-approval .rc-approval-actions")).ToHaveCountAsync(0);
    }
}
