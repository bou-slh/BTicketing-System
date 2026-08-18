using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Areas.Agent.Controllers;
using RapidsolDestek.Web.Identity;

namespace RapidsolDestek.Tests;

/// <summary>
/// S6 agent/profile.html: the vacation assignment guard (StaffAvailability behind the
/// Ata dialogs and topic auto-assignment), the profile save (contact + prefs +
/// signature + culture cookie), the password change, and the 2FA method transitions
/// (TOTP enrollment verify, email method, admin disable guard).
/// </summary>
[Collection("Postgres")]
public class AgentProfileTests(PostgresFixture fixture)
{
    // ---- Vacation assignment guard (ROADMAP §6.2 profile row) ----------------------

    private async Task<Ticket> CreateTicketAsync(ServiceScopeBundle s, int? departmentId = null, int? topicId = null)
    {
        var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
        return await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Tatil modu testi {Guid.NewGuid():N}",
            Body = "<p>içerik</p>",
            DepartmentId = departmentId,
            HelpTopicId = topicId,
        }, owner);
    }

    [Fact]
    public async Task Assign_ToVacationingStaff_IsRefused()
    {
        using var s = new ServiceScopeBundle(fixture);
        var ticket = await CreateTicketAsync(s);
        var uakin = await TestActors.StaffAsync(s.Db, "uakin");
        var saydin = await s.Db.Staff.SingleAsync(x => x.Username == "saydin");
        Assert.True(saydin.OnVacation); // canon seed: Selin Aydın is on vacation

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Get<ITicketService>().AssignAsync(ticket.Id, saydin.Id, null, uakin));
        Assert.Equal(StaffAvailability.OnVacationRuleCode, ex.Code);

        // Assigning to an available agent still works.
        var dkaya = await s.Db.Staff.SingleAsync(x => x.Username == "dkaya");
        await s.Get<ITicketService>().AssignAsync(ticket.Id, dkaya.Id, null, uakin);
    }

    [Fact]
    public async Task Claim_ByVacationingStaff_IsRefused()
    {
        using var s = new ServiceScopeBundle(fixture);
        var bordroId = await s.Db.Departments.Where(d => d.Name == "Bordro").Select(d => d.Id).SingleAsync();
        var ticket = await CreateTicketAsync(s, departmentId: bordroId);
        var saydin = await TestActors.StaffAsync(s.Db, "saydin"); // Bordro member, on vacation

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Get<ITicketService>().ClaimAsync(ticket.Id, saydin));
        Assert.Equal(StaffAvailability.OnVacationRuleCode, ex.Code);
    }

    [Fact]
    public async Task TopicAutoAssign_SkipsVacationingStaff_ButStillRoutes()
    {
        using var s = new ServiceScopeBundle(fixture);
        var bordroId = await s.Db.Departments.Where(d => d.Name == "Bordro").Select(d => d.Id).SingleAsync();
        var saydinId = await s.Db.Staff.Where(x => x.Username == "saydin").Select(x => x.Id).SingleAsync();
        var topic = new HelpTopic
        {
            Name = $"Tatil rota testi {Guid.NewGuid():N}",
            DepartmentId = bordroId,
            StaffId = saydinId,
        };
        s.Db.HelpTopics.Add(topic);
        await s.Db.SaveChangesAsync();

        var ticket = await CreateTicketAsync(s, topicId: topic.Id);

        Assert.Equal(bordroId, ticket.DepartmentId); // routing intact
        Assert.Null(ticket.StaffId);                 // staff pin dropped
    }

    [Fact]
    public async Task TaskAssignAndCreate_ToVacationingStaff_AreRefused()
    {
        using var s = new ServiceScopeBundle(fixture);
        var uakin = await TestActors.StaffAsync(s.Db, "uakin");
        var destekId = await s.Db.Departments.Where(d => d.Name == "Destek").Select(d => d.Id).SingleAsync();
        var saydinId = await s.Db.Staff.Where(x => x.Username == "saydin").Select(x => x.Id).SingleAsync();

        var task = await s.Get<ITaskService>().CreateAsync(new TaskCreateRequest
        {
            Title = $"Tatil görev testi {Guid.NewGuid():N}",
            DepartmentId = destekId,
        }, uakin);

        var assignEx = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Get<ITaskService>().AssignAsync(task.Id, saydinId, null, uakin));
        Assert.Equal(StaffAvailability.OnVacationRuleCode, assignEx.Code);

        var createEx = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Get<ITaskService>().CreateAsync(new TaskCreateRequest
            {
                Title = $"Tatil görev testi {Guid.NewGuid():N}",
                DepartmentId = destekId,
                StaffId = saydinId,
            }, uakin));
        Assert.Equal(StaffAvailability.OnVacationRuleCode, createEx.Code);
    }

    // ---- Profile page behaviors ----------------------------------------------------

    private static ProfileController CreateController(IServiceScope scope, Staff staff)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, staff.IdentityUserId!.Value.ToString())], "test")),
        };
        // SignInManager reads its HttpContext through the accessor (RefreshSignInAsync).
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        return new ProfileController(
            scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>(),
            scope.ServiceProvider.GetRequiredService<StaffSignInManager>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NullTempDataProvider()),
        };
    }

    private static AgentProfileVm ValidVm(Staff staff) => new()
    {
        FirstName = staff.FirstName,
        LastName = staff.LastName,
        Email = staff.Email ?? $"{staff.Username}@rapidsol.com.tr",
        Phone = staff.Phone,
        PhoneExt = staff.PhoneExt,
        Mobile = staff.Mobile,
        TwoFactor = staff.TwoFactorMethod switch
        {
            TwoFactorMethod.App => "app",
            TwoFactorMethod.Email => "email",
            _ => "none",
        },
        OnVacation = staff.OnVacation,
        PageSize = staff.PageSize,
        AutoRefresh = staff.AutoRefreshMinutes,
        DefaultQueue = "open",
        ThreadOrder = "newest",
        SigDefault = "mine",
        TimeZone = "Europe/Istanbul",
        TimeFormat = "24",
        Language = "tr",
        Signature = staff.Signature,
    };

    [Fact]
    public async Task Save_PersistsContactPrefsSignatureVacation_AndSetsCultureCookie()
    {
        using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
            var staff = await db.Staff.SingleAsync(x => x.Username == "adogan");
            var controller = CreateController(scope, staff);

            var vm = ValidVm(staff);
            vm.Mobile = "+90 532 555 0106";
            vm.OnVacation = true;
            vm.PageSize = 50;
            vm.AutoRefresh = 3;
            vm.DefaultQueue = "mine";
            vm.ThreadOrder = "oldest";
            vm.SigDefault = "dept";
            vm.TimeZone = "Europe/Berlin";
            vm.TimeFormat = "12";
            vm.Language = "en";
            vm.Signature = "Aslı Doğan\nDestek Temsilcisi · RapidSol";

            var result = await controller.Index(vm, default);
            var redirect = Assert.IsType<RedirectResult>(result);
            Assert.Equal("/agent/profile", redirect.Url);
            Assert.Equal("pf.toastSaved", controller.TempData["PfToast"]);

            // Language select switches culture server-side on this very response.
            var setCookies = controller.HttpContext.Response.Headers.SetCookie.ToString();
            Assert.Contains(".AspNetCore.Culture", setCookies);
            Assert.Contains("c%3Den", setCookies);
        }

        using var fresh = new ServiceScopeBundle(fixture);
        var saved = await fresh.Db.Staff.SingleAsync(x => x.Username == "adogan");
        Assert.Equal("+90 532 555 0106", saved.Mobile);
        Assert.True(saved.OnVacation);
        Assert.Equal(50, saved.PageSize);
        Assert.Equal(3, saved.AutoRefreshMinutes);
        Assert.Equal(AgentDefaultQueue.Mine, saved.DefaultQueue);
        Assert.False(saved.ThreadOrderNewestFirst);
        Assert.Equal(SignatureType.Department, saved.DefaultSignatureType);
        Assert.Equal("Europe/Berlin", saved.Timezone);
        Assert.False(saved.Use24HourTime);
        Assert.Equal("en", saved.Language);
        Assert.Equal("Aslı Doğan\nDestek Temsilcisi · RapidSol", saved.Signature);

        // The vacation switch immediately guards assignment (ROADMAP row's "has effect").
        var uakin = await TestActors.StaffAsync(fresh.Db, "uakin");
        var ticket = await CreateTicketAsync(fresh);
        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => fresh.Get<ITicketService>().AssignAsync(ticket.Id, saved.Id, null, uakin));
        Assert.Equal(StaffAvailability.OnVacationRuleCode, ex.Code);

        // Restore the shared canon row (adogan is available in §2).
        using (var restore = fixture.CreateScope())
        {
            var db = restore.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
            var staff = await db.Staff.SingleAsync(x => x.Username == "adogan");
            var controller = CreateController(restore, staff);
            var back = ValidVm(staff);
            back.OnVacation = false;
            back.Language = "tr";
            Assert.IsType<RedirectResult>(await controller.Index(back, default));
        }
    }

    [Fact]
    public async Task PasswordChange_ValidatesCurrent_AndStampsChangeDate()
    {
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
        var staff = await db.Staff.SingleAsync(x => x.Username == "dkaya");
        var controller = CreateController(scope, staff);

        // Wrong current password → error toast, no stamp.
        Assert.IsType<RedirectResult>(await controller.Password("yanlış-parola", "yeniParola12", "yeniParola12", default));
        Assert.Equal("pf.errCurrentWrong", controller.TempData["PfToast"]);
        Assert.Null((await db.Staff.AsNoTracking().SingleAsync(x => x.Username == "dkaya")).PasswordChangedAt);

        // Mismatching repeat → error before Identity is involved.
        controller.TempData.Clear();
        Assert.IsType<RedirectResult>(await controller.Password("rapidsol1dev", "yeniParola12", "başkaParola12", default));
        Assert.Equal("pf.errPasswordMismatch", controller.TempData["PfToast"]);

        // Valid change → stamped; then restore the seeded password for the shared fixture.
        controller.TempData.Clear();
        Assert.IsType<RedirectResult>(await controller.Password("rapidsol1dev", "yeniParola12", "yeniParola12", default));
        Assert.Equal("pf.toastPass", controller.TempData["PfToast"]);
        Assert.NotNull((await db.Staff.AsNoTracking().SingleAsync(x => x.Username == "dkaya")).PasswordChangedAt);

        controller.TempData.Clear();
        Assert.IsType<RedirectResult>(await controller.Password("yeniParola12", "rapidsol1dev", "rapidsol1dev", default));
        Assert.Equal("pf.toastPass", controller.TempData["PfToast"]);
    }

    [Fact]
    public async Task TwoFactor_EnrollVerify_EmailMethod_AndDisable()
    {
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var staff = await db.Staff.SingleAsync(x => x.Username == "mcetin");
        var user = (await users.FindByNameAsync("mcetin"))!;
        var controller = CreateController(scope, staff);

        // Selecting the app method without a confirmed enrollment fails loudly.
        var vm = ValidVm(staff);
        vm.TwoFactor = "app";
        var view = Assert.IsType<ViewResult>(await controller.Index(vm, default));
        Assert.Equal("pf.err2faNeedsSetup", view.ViewData["PfToastKey"]);
        Assert.False((await users.FindByNameAsync("mcetin"))!.TwoFactorEnabled);

        // A wrong enrollment code does not enable anything.
        controller.TempData.Clear();
        Assert.IsType<RedirectResult>(await controller.Enable2fa("000000", default));
        Assert.Equal("pf.err2faCode", controller.TempData["PfToast"]);
        Assert.False((await users.FindByNameAsync("mcetin"))!.TwoFactorEnabled);

        // Yapılandır enrollment: verify a real TOTP code computed from the dialog's key.
        await users.ResetAuthenticatorKeyAsync(user);
        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        controller.TempData.Clear();
        Assert.IsType<RedirectResult>(await controller.Enable2fa(ComputeTotp(key), default));
        Assert.Equal("pf.toast2faOn", controller.TempData["PfToast"]);
        Assert.True((await users.FindByNameAsync("mcetin"))!.TwoFactorEnabled);
        Assert.Equal(TwoFactorMethod.App,
            await db.Staff.AsNoTracking().Where(x => x.Username == "mcetin").Select(x => x.TwoFactorMethod).SingleAsync());

        // Switching to the email code keeps 2FA on with the email provider.
        controller.TempData.Clear();
        vm.TwoFactor = "email";
        Assert.IsType<RedirectResult>(await controller.Index(vm, default));
        Assert.True((await users.FindByNameAsync("mcetin"))!.TwoFactorEnabled);
        Assert.Equal(TwoFactorMethod.Email,
            await db.Staff.AsNoTracking().Where(x => x.Username == "mcetin").Select(x => x.TwoFactorMethod).SingleAsync());

        // Non-admins may disable; the shared fixture ends with the seeded state.
        controller.TempData.Clear();
        vm.TwoFactor = "none";
        Assert.IsType<RedirectResult>(await controller.Index(vm, default));
        Assert.False((await users.FindByNameAsync("mcetin"))!.TwoFactorEnabled);
        Assert.Equal(TwoFactorMethod.None,
            await db.Staff.AsNoTracking().Where(x => x.Username == "mcetin").Select(x => x.TwoFactorMethod).SingleAsync());
    }

    [Fact]
    public async Task TwoFactor_AdminCannotDisable()
    {
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
        var staff = await db.Staff.SingleAsync(x => x.Username == "uakin");
        Assert.Equal(TwoFactorMethod.App, staff.TwoFactorMethod); // seeded from the dev TOTP key
        var controller = CreateController(scope, staff);

        var vm = ValidVm(staff);
        vm.TwoFactor = "none";
        var view = Assert.IsType<ViewResult>(await controller.Index(vm, default));
        Assert.Equal("pf.err2faAdminRequired", view.ViewData["PfToastKey"]);

        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        Assert.True((await users.FindByNameAsync("uakin"))!.TwoFactorEnabled);
    }

    [Fact]
    public async Task ProfilePage_RendersThroughTheRealPipeline()
    {
        // Full-stack smoke: real login (antiforgery + cookie), then the profile page —
        // catches Razor runtime issues the direct controller tests can't see.
        var client = fixture.Factory.CreateClient();
        var loginPage = await client.GetStringAsync("/agent/login");
        var token = System.Text.RegularExpressions.Regex
            .Match(loginPage, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var login = await client.PostAsync("/agent/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["User"] = "kyilmaz",
            ["Password"] = "rapidsol1dev",
            ["__RequestVerificationToken"] = token,
        }));
        Assert.True(login.IsSuccessStatusCode); // redirect followed to the dashboard

        var html = await client.GetStringAsync("/agent/profile");
        Assert.Contains("pf-panel-account", html);
        Assert.Contains("pf-panel-prefs", html);
        Assert.Contains("pf-panel-sig", html);
        Assert.Contains("dlg-pass", html);
        Assert.Contains("dlg-2fa", html);
        Assert.Contains("data-lang-switch", html);
        Assert.Contains("data-nav=\"profile\"", html);
    }

    // ---- RFC 6238 helper (mirrors Identity's AuthenticatorTokenProvider math) ------

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
        foreach (var c in input.TrimEnd('='))
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

    /// <summary>Minimal ITempDataProvider so controller TempData works without a session.</summary>
    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
