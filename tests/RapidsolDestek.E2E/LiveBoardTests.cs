using System.Diagnostics;
using Microsoft.Playwright;

namespace RapidsolDestek.E2E;

/// <summary>
/// S6 exit gate, live-board leg (ROADMAP §5): board updates propagate in under one
/// second across two browsers. Two separate browser contexts (dkaya + kyilmaz, both
/// Destek) sit on /agent/live; dkaya claims an unassigned card ("Üstlen") and
/// kyilmaz's board must show the assignment ticker item within 1 s of the click.
/// Runs against a RUNNING app (E2E_BASE_URL, default http://localhost:5310).
/// Mutates seeded canon (the claimed ticket gains an assignee) — reseed afterwards
/// for a pristine demo DB.
/// </summary>
[Trait("Category", "E2E")]
public class LiveBoardTests : IAsyncLifetime
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

    private async Task<IPage> LoginAsync(string username)
    {
        var page = await (await _browser.NewContextAsync()).NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/agent/login");
        await page.FillAsync("#lg-user", username);
        await page.FillAsync("#lg-pass", SeedPassword);
        await page.ClickAsync("form button[type=submit], form button.rc-button-primary");
        await page.WaitForURLAsync($"{BaseUrl}/agent/**");
        return page;
    }

    [Fact]
    public async Task Claim_PropagatesToTheSecondBrowser_InUnderOneSecond()
    {
        // Separate cookie universes = two staff sessions (GoldenPath idiom).
        var a = await LoginAsync("dkaya");
        var b = await LoginAsync("kyilmaz");

        foreach (var page in new[] { a, b })
        {
            await page.GotoAsync($"{BaseUrl}/agent/live");
            // Board rendered + SignalR hub handshake done.
            await page.WaitForSelectorAsync("#lv-board[data-connected]");
        }

        // First claimable card visible on BOTH boards (Yeni/Atanmamış columns);
        // picked dynamically so repeated runs against a dirty DB still find one.
        var candidates = await a.Locator(
                "[data-col='new'] .lb-card, [data-col='unassigned'] .lb-card")
            .EvaluateAllAsync<string[]>("cards => cards.map(c => c.dataset.number)");
        string? number = null;
        foreach (var n in candidates)
        {
            if (await b.Locator($".lb-card[data-number='{n}']").CountAsync() > 0)
            {
                number = n;
                break;
            }
        }
        Assert.False(string.IsNullOrEmpty(number),
            "No unassigned card visible to both dkaya and kyilmaz — reseed the dev DB.");

        var cardOnA = a.Locator($".lb-card[data-number='{number}']").First;
        var tickerOnB = b.Locator($"#lv-ticker .item[data-kind='assigned']", new() { HasText = number! });

        var sw = Stopwatch.StartNew();
        await cardOnA.Locator("[data-claim]").ClickAsync();

        // THE GATE: the other browser's board reflects the mutation in <1 s.
        await Assertions.Expect(tickerOnB).ToBeVisibleAsync(new() { Timeout = 1000 });
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Propagation took {sw.ElapsedMilliseconds} ms (>1000)");

        // The claimed card also left B's claimable columns once the refetch landed
        // (claiming a Yeni card transitions new→open, so it leaves that column too).
        await Assertions.Expect(b.Locator(
                $"[data-col='new'] .lb-card[data-number='{number}'], " +
                $"[data-col='unassigned'] .lb-card[data-number='{number}']"))
            .ToHaveCountAsync(0, new() { Timeout = 2000 });

        // And A's own board converged the same way.
        await Assertions.Expect(a.Locator(
                $"[data-col='new'] .lb-card[data-number='{number}'], " +
                $"[data-col='unassigned'] .lb-card[data-number='{number}']"))
            .ToHaveCountAsync(0, new() { Timeout = 2000 });
    }
}
