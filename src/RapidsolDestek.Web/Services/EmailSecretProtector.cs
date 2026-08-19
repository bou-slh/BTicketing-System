using Microsoft.AspNetCore.DataProtection;

namespace RapidsolDestek.Web.Services;

/// <summary>
/// Secret storage for EmailChannel credentials (S7 admin/email-edit, documented
/// choice): ASP.NET Core DataProtection-encrypted columns
/// (EmailChannel.PasswordProtected / OAuthClientSecretProtected) under a fixed
/// purpose string. Secrets are write-only in the UI — views render a bullet
/// sentinel, never plaintext; a posted empty/bullets-only value means "unchanged".
/// FLAGGED: ciphertexts are bound to the app's DataProtection keyring — rotating
/// or losing the keyring invalidates stored mail credentials (re-enter to fix).
/// </summary>
public interface IEmailSecretProtector
{
    string Protect(string plaintext);

    /// <summary>Null when the payload cannot be decrypted (e.g. keyring rotated).</summary>
    string? Unprotect(string? ciphertext);
}

public sealed class EmailSecretProtector(IDataProtectionProvider provider) : IEmailSecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("RapidsolDestek.EmailChannel.Secrets");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
            return null;
        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
