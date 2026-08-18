using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using RapidsolDestek.Infrastructure.Identity;

namespace RapidsolDestek.Web.Identity;

/// <summary>
/// Password-reset token provider for the staff principal with a short lifespan, so the
/// agent/admin pwreset pages' "Bağlantı 30 dakika geçerlidir" help line is honest
/// (Identity's default DataProtection provider would keep links valid for a day).
/// The customer principal stays on the default provider — portal copy is a separate
/// canon item (its pw.help promises 1 hour).
/// </summary>
public sealed class StaffResetTokenProvider(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<StaffResetTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<StaffUser>> logger)
    : DataProtectorTokenProvider<StaffUser>(dataProtectionProvider, options, logger)
{
    public const string ProviderName = "StaffReset";
}

public sealed class StaffResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public StaffResetTokenProviderOptions()
    {
        Name = StaffResetTokenProvider.ProviderName;
        // TODO(S7): settings-agents consumes — sa.resetWindow (default 30) will own this
        // value; Program.cs overrides from config StaffAuth:ResetWindowMinutes until then.
        TokenLifespan = TimeSpan.FromMinutes(30);
    }
}
