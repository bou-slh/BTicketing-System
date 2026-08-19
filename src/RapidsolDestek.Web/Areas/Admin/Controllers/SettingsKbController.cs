using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin knowledge-base settings (mockups/admin/settings-kb.html, ROADMAP §6.3 + §4 B3):
/// three switches over the "kb" namespace via <see cref="ISettingsService"/>.
/// LIVE: enable_kb is the master switch — while off the portal KB routes return 404
/// and the portal nav/home KB surfaces disappear (KbController + _PortalHeader +
/// portal Home); enable_canned hides the agent composers' canned-response menu and
/// the canned insert endpoints refuse canned ids. Persisted-only: require_login
/// (the portal KB already sits behind the PortalUser cookie — KbSettings annotation).
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class SettingsKbController(ISettingsService settings) : Controller
{
    [HttpGet("/admin/settings-kb")]
    [NavKey("settings-kb")]
    public async Task<IActionResult> Index(CancellationToken ct = default) =>
        View(await settings.GetKbAsync(ct));

    [HttpPost("/admin/settings-kb")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CancellationToken ct = default)
    {
        bool Chk(string name) => Request.Form[name].Contains("true");

        await settings.SetAsync("kb", "enable_kb", Chk("enable_kb").ToString(), ct);
        await settings.SetAsync("kb", "require_login", Chk("require_login").ToString(), ct);
        await settings.SetAsync("kb", "enable_canned", Chk("enable_canned").ToString(), ct);

        TempData["SkbToast"] = "skb.toastSaved";
        return Redirect("/admin/settings-kb");
    }
}
