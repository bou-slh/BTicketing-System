using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

[Area("Agent")]
[Authorize(Policy = "Staff")]
public class DashboardController : Controller
{
    [HttpGet("/agent")]
    [HttpGet("/agent/dashboard")]
    [NavKey("dashboard")]
    public IActionResult Index() => View();
}
