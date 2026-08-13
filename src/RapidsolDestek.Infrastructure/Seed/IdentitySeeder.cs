using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Infrastructure.Identity;

namespace RapidsolDestek.Infrastructure.Seed;

/// <summary>
/// Idempotent dev/demo seed. Users mirror the mockup canon (mockups/ROADMAP.md §2) —
/// illustrative sample data only, replaced by real accounts at launch.
/// </summary>
public static class IdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration config)
    {
        var roles = services.GetRequiredService<RoleManager<StaffRole>>();
        var staff = services.GetRequiredService<UserManager<StaffUser>>();
        var customers = services.GetRequiredService<UserManager<CustomerUser>>();

        var password = config["Seed:Password"] ?? "rapidsol1dev";
        var adminTotpKey = config["Seed:AdminTotpKey"];

        foreach (var role in new[] { "Admin", "Agent" })
        {
            if (await roles.FindByNameAsync(role) is null)
                await roles.CreateAsync(new StaffRole { Name = role });
        }

        // Canon staff roster (agent/directory.html is the authority).
        (string User, string FullName, bool Admin)[] canonStaff =
        [
            ("uakin", "Ümit Yaşar Akın", true),
            ("mcetin", "Merve Çetin", false),
            ("dkaya", "Deniz Kaya", false),
            ("saydin", "Selin Aydın", false),
            ("kyilmaz", "Kerem Yılmaz", false),
            ("adogan", "Aslı Doğan", false),
        ];

        foreach (var s in canonStaff)
        {
            var user = await staff.FindByNameAsync(s.User);
            if (user is null)
            {
                user = new StaffUser
                {
                    UserName = s.User,
                    Email = $"{Fold(s.User)}@rapidsol.com.tr",
                    EmailConfirmed = true,
                    FullName = s.FullName,
                };
                var created = await staff.CreateAsync(user, password);
                if (!created.Succeeded)
                    throw new InvalidOperationException($"Seed failed for {s.User}: {string.Join("; ", created.Errors.Select(e => e.Description))}");
            }

            if (!await staff.IsInRoleAsync(user, "Agent"))
                await staff.AddToRoleAsync(user, "Agent");
            if (s.Admin && !await staff.IsInRoleAsync(user, "Admin"))
                await staff.AddToRoleAsync(user, "Admin");
            if (!(await staff.GetClaimsAsync(user)).Any(c => c.Type == "FullName"))
                await staff.AddClaimAsync(user, new System.Security.Claims.Claim("FullName", s.FullName));

            // Fixed authenticator key for the seeded admin in dev so tests can compute TOTP codes.
            if (s.Admin && !string.IsNullOrEmpty(adminTotpKey) && !user.TwoFactorEnabled)
            {
                await staff.SetAuthenticationTokenAsync(user, "[AspNetUserStore]", "AuthenticatorKey", adminTotpKey);
                await staff.SetTwoFactorEnabledAsync(user, true);
            }
        }

        // Canon hero customer: Bourla Salehi · Ulaşım A.Ş.
        var bourla = await customers.FindByEmailAsync("bourla.salehi@ulasim.com.tr");
        if (bourla is null)
        {
            bourla = new CustomerUser
            {
                UserName = "bourla.salehi@ulasim.com.tr",
                Email = "bourla.salehi@ulasim.com.tr",
                EmailConfirmed = true,
                FullName = "Bourla Salehi",
                OrganizationName = "Ulaşım A.Ş.",
            };
            var created = await customers.CreateAsync(bourla, password);
            if (!created.Succeeded)
                throw new InvalidOperationException($"Seed failed for Bourla: {string.Join("; ", created.Errors.Select(e => e.Description))}");
        }
        var bourlaClaims = await customers.GetClaimsAsync(bourla);
        if (!bourlaClaims.Any(c => c.Type == "FullName"))
            await customers.AddClaimAsync(bourla, new System.Security.Claims.Claim("FullName", "Bourla Salehi"));
        if (!bourlaClaims.Any(c => c.Type == "OrganizationName"))
            await customers.AddClaimAsync(bourla, new System.Security.Claims.Claim("OrganizationName", "Ulaşım A.Ş."));
    }

    // uakin -> uakin (usernames are already ASCII); guards against future non-ASCII usernames.
    private static string Fold(string s) => s
        .Replace("ç", "c").Replace("ğ", "g").Replace("ı", "i")
        .Replace("ö", "o").Replace("ş", "s").Replace("ü", "u");
}
