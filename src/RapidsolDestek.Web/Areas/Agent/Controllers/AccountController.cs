using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Web.Areas.Portal.Controllers;
using RapidsolDestek.Web.Controllers;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

[Area("Agent")]
public class AccountController(
    UserManager<StaffUser> users,
    StaffSignInManager signIn,
    IAppEmailSender mail,
    IWebHostEnvironment env,
    RapidsolDestek.Infrastructure.AppDbContext db) : StaffAccountControllerBase(users, signIn, mail, env, db)
{
    protected override string AreaPrefix => "/agent";
    protected override bool RequireAdmin => false;

    // Agent panel has its own new-password card (rd-field conventions, no portal step captions).
    protected override string PwresetNewViewName => "PwresetNew";

    [HttpGet("/agent/login")]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl) => View(new StaffLoginVm { ReturnUrl = returnUrl });

    [HttpPost("/agent/login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Login(StaffLoginVm vm) => LoginCore(vm);

    [HttpGet("/agent/login/2fa")]
    [AllowAnonymous]
    public IActionResult Login2fa(string? returnUrl) => View(new StaffLogin2faVm { ReturnUrl = returnUrl });

    [HttpPost("/agent/login/2fa")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Login2fa(StaffLogin2faVm vm) => Login2faCore(vm);

    [HttpPost("/agent/logout")]
    [Authorize(Policy = "Staff")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Logout() => LogoutCore();

    [HttpGet("/agent/pwreset")]
    [AllowAnonymous]
    public IActionResult Pwreset() => View();

    [HttpPost("/agent/pwreset")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Pwreset(string email) => PwresetCore(email);

    [HttpGet("/agent/pwreset/new")]
    [AllowAnonymous]
    public IActionResult PwresetNew(string email, string token) => PwresetNewCore(email, token);

    [HttpPost("/agent/pwreset/new")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> PwresetNew(PwresetNewVm vm) => PwresetNewPostCore(vm);
}
