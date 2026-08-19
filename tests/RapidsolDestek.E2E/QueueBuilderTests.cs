using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Playwright;

namespace RapidsolDestek.E2E;

/// <summary>
/// S7 exit gate, leg 2 (ROADMAP §5): a builder-created queue demonstrably affects
/// the agent panel. Admin uakin (mandatory TOTP, Seed:AdminTotpKey) builds a shared
/// queue through the real /admin/queues UI — criteria rows dept=Bordro +
/// priority=emergency under the "Açık" parent, live preview checked BEFORE saving —
/// then agent saydin (Bordro) finds the queue in the tickets queue tree and opening
/// it lists exactly the matching seeded ticket (R716536) and not a non-matching one.
/// Runs against a RUNNING app (E2E_BASE_URL, default http://localhost:5310).
/// Leaves a custom SavedQueue behind — reseed for a pristine demo.
/// </summary>
[Trait("Category", "E2E")]
public class QueueBuilderTests : IAsyncLifetime
{
    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://localhost:5310";

    private static readonly string SeedPassword =
        Environment.GetEnvironmentVariable("SEED_PASSWORD") ?? "rapidsol1dev";

    /// <summary>Dev TOTP key (appsettings.Development.json Seed:AdminTotpKey).</summary>
    private static readonly string AdminTotpKey =
        Environment.GetEnvironmentVariable("SEED_ADMIN_TOTP_KEY") ?? "RAPIDSOLDESTEKDEVKEY234567ABCDEF";

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
    public async Task AdminBuildsQueue_AgentSeesItInTreeAndItListsTheRightTickets()
    {
        var sw = Stopwatch.StartNew();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var queueName = $"E2E Acil Bordro {unique}";

        var admin = await (await _browser.NewContextAsync()).NewPageAsync();
        var agent = await (await _browser.NewContextAsync()).NewPageAsync();

        // -- 1. Admin: uakin logs in (password + mandatory TOTP) ---------------
        await admin.GotoAsync($"{BaseUrl}/admin/login");
        await admin.FillAsync("#lg-user", "uakin");
        await admin.FillAsync("#lg-pass", SeedPassword);
        await admin.ClickAsync("form button[type=submit]");
        await admin.WaitForURLAsync($"{BaseUrl}/admin/login/2fa**");
        await admin.FillAsync("input[name=Code]", ComputeTotp(AdminTotpKey));
        await admin.ClickAsync("form button[type=submit]");
        await admin.WaitForURLAsync($"{BaseUrl}/admin/dashboard**");

        // -- 2. Admin: builds the queue through the real builder UI ------------
        await admin.GotoAsync($"{BaseUrl}/admin/queues?id=0"); // create mode
        await admin.FillAsync("#q-name", queueName);
        await admin.SelectOptionAsync("#q-parent", new SelectOptionValue { Label = "Açık" });

        // Criteria row 1: Departman şudur Bordro.
        await admin.ClickAsync("[data-rule-add='qb-crit-tpl']");
        var row1 = admin.Locator("[data-cf-row]").Last;
        await row1.Locator("select[name=cfF]").SelectOptionAsync("dept");
        await row1.Locator("[data-cf-val='dept']").SelectOptionAsync(new SelectOptionValue { Label = "Bordro" });

        // Criteria row 2: Öncelik şudur Acil.
        await admin.ClickAsync("[data-rule-add='qb-crit-tpl']");
        var row2 = admin.Locator("[data-cf-row]").Last;
        await row2.Locator("select[name=cfF]").SelectOptionAsync("priority");
        await row2.Locator("[data-cf-val='priority']").SelectOptionAsync("emergency");

        // -- 3. Live preview reflects the UNSAVED criteria through the engine ---
        await admin.ClickAsync("[data-tab='qb-preview']");
        var preview = admin.Locator("#qb-preview-body");
        await Assertions.Expect(preview).ToContainTextAsync("R716536"); // Bordro + Acil
        await Assertions.Expect(preview).Not.ToContainTextAsync("R716561"); // Bordro but Düşük

        // -- 4. Save: PRG back to the builder in edit mode ----------------------
        await admin.ClickAsync("#qb-form button[type=submit]");
        await admin.WaitForURLAsync($"{BaseUrl}/admin/queues?id=*");
        await Assertions.Expect(admin.Locator("#q-name")).ToHaveValueAsync(queueName);

        // -- 5. Agent saydin: the queue appears in the tickets tree -------------
        await agent.GotoAsync($"{BaseUrl}/agent/login");
        await agent.FillAsync("#lg-user", "saydin");
        await agent.FillAsync("#lg-pass", SeedPassword);
        await agent.ClickAsync("form button[type=submit], form button.rc-button-primary");
        await agent.WaitForURLAsync($"{BaseUrl}/agent/**");

        await agent.GotoAsync($"{BaseUrl}/agent/tickets");
        var treeLink = agent.Locator($".bo-nav-group a:has-text('{queueName}')");
        await Assertions.Expect(treeLink).ToBeVisibleAsync();

        // -- 6. Opening it lists exactly the matching tickets --------------------
        await treeLink.ClickAsync();
        await agent.WaitForURLAsync($"{BaseUrl}/agent/tickets?queue=*");
        await Assertions.Expect(agent.Locator($".bo-nav-group a.active:has-text('{queueName}')"))
            .ToBeVisibleAsync();
        var list = agent.Locator("table.rd-table tbody");
        await Assertions.Expect(list).ToContainTextAsync("R716536");
        await Assertions.Expect(list).ToContainTextAsync("Maaş ödeme dosyası bankaya gitmedi");
        await Assertions.Expect(list).Not.ToContainTextAsync("R716561"); // filtered out

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(3), $"gate leg took {sw.Elapsed}");
    }

    // ---- totp helper (unit-suite twin) ---------------------------------------------------

    private static string ComputeTotp(string base32Key)
    {
        var keyBytes = FromBase32(base32Key.Replace(" ", "").ToUpperInvariant());
        var timestep = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var timestepBytes = BitConverter.GetBytes(timestep);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(timestepBytes);
        using var hmac = new HMACSHA1(keyBytes);
        var hash = hmac.ComputeHash(timestepBytes);
        var offset = hash[^1] & 0xf;
        var binary = ((hash[offset] & 0x7f) << 24) | ((hash[offset + 1] & 0xff) << 16)
                   | ((hash[offset + 2] & 0xff) << 8) | (hash[offset + 3] & 0xff);
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] FromBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in input)
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }
        return [.. output];
    }
}
