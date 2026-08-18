using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Extensions;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Seed;
using RapidsolDestek.Web;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;
using RapidsolDestek.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- MVC + localization ----------------------------------------------------

builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.AddControllersWithViews()
    .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
    .AddDataAnnotationsLocalization(o =>
        o.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResources)));

builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    var cultures = new[] { new CultureInfo("tr"), new CultureInfo("en") };
    o.DefaultRequestCulture = new RequestCulture("tr");
    o.SupportedCultures = cultures;
    o.SupportedUICultures = cultures;
    // Cookie only: deterministic TR default regardless of browser Accept-Language.
    o.RequestCultureProviders = [new CookieRequestCultureProvider()];
});

// ---- Data ------------------------------------------------------------------

builder.Services.AddRapidsolData(builder.Configuration);
builder.Services.AddRapidsolServices(builder.Configuration);

// ---- AuthN: two principals, separate cookie schemes ------------------------

builder.Services.AddAuthentication("Contextual")
    // Default scheme: pick the cookie matching the URL space, so HttpContext.User
    // (and antiforgery identity binding) is populated on every request, not only
    // behind [Authorize] policies.
    .AddPolicyScheme("Contextual", "Contextual", o =>
    {
        o.ForwardDefaultSelector = ctx =>
            ctx.Request.Path.StartsWithSegments("/agent") || ctx.Request.Path.StartsWithSegments("/admin")
                ? AuthSchemes.Staff
                : AuthSchemes.Customer;
    })
    .AddCookie(AuthSchemes.Customer, o =>
    {
        o.Cookie.Name = "rd.customer";
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/login";
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
    })
    .AddCookie(AuthSchemes.Staff, o =>
    {
        o.Cookie.Name = "rd.staff";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx =>
        {
            var login = ctx.Request.Path.StartsWithSegments("/admin") ? "/admin/login" : "/agent/login";
            ctx.Response.Redirect($"{login}?returnUrl={Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString)}");
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            // Admin area without admin rights / without mfa → admin login (which enforces both).
            // Exception: an authenticated admin whose session lacks amr=mfa is mid-enrollment
            // (password-only sign-in of a no-2FA admin) — send them to finish the mandatory
            // setup instead of the login form (B6 mandatory 2FA).
            var user = ctx.HttpContext.User;
            if (ctx.Request.Path.StartsWithSegments("/admin")
                && user.Identity?.IsAuthenticated == true
                && user.IsInRole("Admin") && !user.HasClaim("amr", "mfa"))
            {
                ctx.Response.Redirect("/admin/2fa-setup");
                return Task.CompletedTask;
            }
            var login = ctx.Request.Path.StartsWithSegments("/admin") ? "/admin/login" : "/agent/login";
            ctx.Response.Redirect(login);
            return Task.CompletedTask;
        };
    })
    .AddCookie(IdentityConstants.TwoFactorUserIdScheme)
    .AddCookie(IdentityConstants.TwoFactorRememberMeScheme);

static void ConfigureIdentity(IdentityOptions o)
{
    // Mockup help text: at least 8 characters, letters and digits.
    o.Password.RequiredLength = 8;
    o.Password.RequireDigit = true;
    o.Password.RequireLowercase = true;
    o.Password.RequireUppercase = false;
    o.Password.RequireNonAlphanumeric = false;
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    o.User.RequireUniqueEmail = true;
}

builder.Services.AddIdentityCore<CustomerUser>(ConfigureIdentity)
    .AddUserStore<CustomerStore>()
    .AddSignInManager<CustomerSignInManager>()
    .AddDefaultTokenProviders();

// Staff reset links expire per the mockups' pw.help ("30 dakika") instead of the 1-day default.
// TODO(S7): settings-agents consumes — sa.resetWindow will own this value when that page ports.
builder.Services.Configure<StaffResetTokenProviderOptions>(o =>
    o.TokenLifespan = TimeSpan.FromMinutes(builder.Configuration.GetValue("StaffAuth:ResetWindowMinutes", 30)));

builder.Services.AddIdentityCore<StaffUser>(o =>
    {
        ConfigureIdentity(o);
        // Staff usernames are short handles (uakin); customers use email as username.
        o.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
        o.Tokens.PasswordResetTokenProvider = StaffResetTokenProvider.ProviderName;
    })
    .AddRoles<StaffRole>()
    .AddUserStore<StaffStore>()
    .AddRoleStore<StaffRoleStore>()
    .AddSignInManager<StaffSignInManager>()
    .AddDefaultTokenProviders()
    .AddTokenProvider<StaffResetTokenProvider>(StaffResetTokenProvider.ProviderName);

// ---- AuthZ policies --------------------------------------------------------

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("PortalUser", p => p.AddAuthenticationSchemes(AuthSchemes.Customer).RequireAuthenticatedUser());
    o.AddPolicy("Staff", p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireAuthenticatedUser());
    o.AddPolicy("AdminOnly", p => p.AddAuthenticationSchemes(AuthSchemes.Staff)
        .RequireAuthenticatedUser()
        .RequireRole("Admin")
        .RequireClaim("amr", "mfa"));
});

// ---- App services ----------------------------------------------------------

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache(); // agent tickets queue-count cache (S6)
builder.Services.AddSignalR(); // B7 live board (agent/live)
builder.Services.AddScoped<ISidebarBadgeService, SidebarBadgeService>();
builder.Services.AddSingleton<IAppEmailSender, DevLoggingEmailSender>();

// Interim B8 effort emails (S8 replaces the transport, the handler contract stays).
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.EffortProposed>, EffortEmailHandler>();
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.EffortRevised>, EffortEmailHandler>();
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.EffortApproved>, EffortEmailHandler>();
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.EffortRejected>, EffortEmailHandler>();

// B7 live board: domain events → LiveBoardHub broadcasts (EffortEmailHandler precedent).
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.TicketCreated>, LiveBoardHandler>();
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.TicketAssigned>, LiveBoardHandler>();
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.TicketStatusChanged>, LiveBoardHandler>();
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.TicketTransferred>, LiveBoardHandler>();
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.ThreadEntryAdded>, LiveBoardHandler>();
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.EffortProposed>, LiveBoardHandler>();
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.EffortRevised>, LiveBoardHandler>();
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.EffortWithdrawn>, LiveBoardHandler>();
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.EffortApproved>, LiveBoardHandler>();
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.EffortRejected>, LiveBoardHandler>();
builder.Services.AddScoped<RapidsolDestek.Infrastructure.Events.IDomainEventHandler<RapidsolDestek.Domain.Events.TicketOverdue>, LiveBoardHandler>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/offline");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.MapStaticAssets();
app.UseRequestLocalization();
// Before routing so the rewritten path (portal → /offline) is what gets routed.
app.UseMiddleware<MaintenanceModeMiddleware>();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers().WithStaticAssets();
// Under /agent so the Contextual scheme picks the staff cookie and the
// maintenance middleware's portal rewrite never touches the hub path.
app.MapHub<RapidsolDestek.Web.Hubs.LiveBoardHub>(RapidsolDestek.Web.Hubs.LiveBoardHub.Path);

// Dev bootstrap: migrate + seed (idempotent).
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    await IdentitySeeder.SeedAsync(scope.ServiceProvider, app.Configuration);
    await DomainSeeder.SeedAsync(scope.ServiceProvider);
}

app.Run();

public partial class Program;
