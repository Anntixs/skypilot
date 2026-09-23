using System.Security.Cryptography;
using System.Text;
using SkyPilot.Core.Settings;

namespace SkyPilot.App.Services;

/// <summary>Encrypts the stored password for the current Windows user.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    public string Protect(string plain) => plain.Length == 0
        ? ""
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedValue)
    {
        if (protectedValue.Length == 0) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null,
                DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return "";
        }
    }
}
