using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace SubVid.Server.Cloud;

public sealed class CloudCredentialProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector(
        "SubVid.Server.CloudProviderCredential.v1");

    public string Protect(string apiKey)
    {
        var normalized = Normalize(apiKey);
        return _protector.Protect(normalized);
    }

    public string Unprotect(string encryptedApiKey) =>
        _protector.Unprotect(encryptedApiKey);

    public static string Fingerprint(string apiKey)
    {
        var normalized = Normalize(apiKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    public static string Suffix(string apiKey)
    {
        var normalized = Normalize(apiKey);
        return normalized.Length <= 6 ? normalized : normalized[^6..];
    }

    private static string Normalize(string apiKey)
    {
        var normalized = (apiKey ?? string.Empty).Trim();
        if (normalized.Length is < 8 or > 1000
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("API key không hợp lệ.", nameof(apiKey));
        }

        return normalized;
    }
}
