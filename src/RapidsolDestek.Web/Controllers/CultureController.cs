using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace RapidsolDestek.Web.Controllers;

public class CultureController : Controller
{
    private static readonly string[] Supported = ["tr", "en"];

    [HttpPost("/culture")]
    [ValidateAntiForgeryToken]
    public IActionResult Set(string culture, string? returnUrl)
    {
        if (!Supported.Contains(culture))
            return BadRequest();

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), Path = "/", IsEssential = true });

        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
}
