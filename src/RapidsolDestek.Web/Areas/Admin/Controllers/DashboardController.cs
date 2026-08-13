using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class DashboardController : Controller
{
    [HttpGet("/admin")]
    [HttpGet("/admin/dashboard")]
    [NavKey("dashboard")]
    public IActionResult Index() => View();
}
