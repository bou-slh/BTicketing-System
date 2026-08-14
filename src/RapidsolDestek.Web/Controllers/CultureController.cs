using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace RapidsolDestek.Web.Controllers;

public class CultureController : Controller
{
    public static readonly string[] Supported = ["tr", "en"];

    /// <summary>
    /// Writes the culture cookie the CookieRequestCultureProvider reads (Program.cs);
    /// shared with the profile save + login so a persisted language preference takes
    /// effect on the very next response.
    /// </summary>
    public static void ApplyCultureCookie(HttpResponse response, string culture) =>
        response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), Path = "/", IsEssential = true });

    [HttpPost("/culture")]
    [ValidateAntiForgeryToken]
    public IActionResult Set(string culture, string? returnUrl)
    {
        if (!Supported.Contains(culture))
            return BadRequest();

        ApplyCultureCookie(Response, culture);

        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
}
