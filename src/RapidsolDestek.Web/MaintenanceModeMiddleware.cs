using RapidsolDestek.Infrastructure.Services;

namespace RapidsolDestek.Web;

/// <summary>
/// Maintenance ("offline") mode — osTicket semantics: when the system/offline
/// setting is on, the client portal serves portal/offline.html for every portal
/// route while the staff spaces (/agent, /admin — including their logins) stay
/// reachable. Default is off (seeded system/offline=false; a missing row also
/// counts as off).
/// </summary>
// S7: admin/settings-system.html "Yardım masası çevrimiçi" edits this SAME
// system/offline setting (inverted: online ⇔ !offline) — SettingsSystemController.
public class MaintenanceModeMiddleware(RequestDelegate next)
{
    public const string SettingNamespace = "system";
    public const string SettingKey = "offline";

    public async Task InvokeAsync(HttpContext ctx, ISettingsService settings)
    {
        if (IsPortalPath(ctx.Request.Path) &&
            bool.TryParse(await settings.GetAsync(SettingNamespace, SettingKey, ctx.RequestAborted), out var off) && off)
        {
            // Re-route (not redirect) to the offline page: the URL stays put and
            // comes back once maintenance ends. Mutations are swallowed into a GET.
            ctx.Request.Path = "/offline";
            ctx.Request.Method = HttpMethods.Get;
        }
        await next(ctx);
    }

    /// <summary>
    /// True for routes that belong to the client portal. Staff spaces, the
    /// offline page itself, the culture switcher (the offline page has a
    /// language select) and static assets (dotted last segment) pass through.
    /// </summary>
    public static bool IsPortalPath(PathString path)
    {
        if (path.StartsWithSegments("/agent") ||
            path.StartsWithSegments("/admin") ||
            path.StartsWithSegments("/offline") ||
            path.StartsWithSegments("/culture"))
            return false;

        // Static assets: /css/*.css, /js/*.js, /favicon.svg …
        var value = path.Value ?? "/";
        var lastSegment = value[(value.LastIndexOf('/') + 1)..];
        return !lastSegment.Contains('.');
    }
}
