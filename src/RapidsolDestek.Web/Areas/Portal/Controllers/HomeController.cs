using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Portal.Controllers;

[Area("Portal")]
[Authorize(Policy = "PortalUser")]
public class HomeController : Controller
{
    [HttpGet("/")]
    [NavKey("home")]
    public IActionResult Index() => View();
}
