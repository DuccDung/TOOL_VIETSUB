using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SubVid.App.Core;

namespace SubVid.App.Api;

public sealed class ProtectedVoiceCredentialStore
{
    public const string FptProvider = "fpt";
    // Compatibility identifier: changing this would make existing DPAPI-protected keys unreadable.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TOOL_VIETSUB_VOICE_KEYS_V1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _credentialPath;

    public ProtectedVoiceCredentialStore(AppPaths paths)
    {
        _credentialPath = Path.Combine(paths.RootDirectory, "voice.credentials");
    }

    public bool HasFptKey => !string.IsNullOrWhiteSpace(GetFptKey());

    public string? GetFptKey()
    {
        var credentials = Load();
        return credentials.TryGetValue(FptProvider, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    public void SaveFptKey(string apiKey)
    {
        var key = (apiKey ?? string.Empty).Trim();
        if (key.Length is < 8 or > 512 || key.Any(char.IsControl))
        {
            throw new ArgumentException("API key FPT.AI không hợp lệ.", nameof(apiKey));
        }

        var credentials = Load();
        credentials[FptProvider] = key;
        Save(credentials);
    }

    public void DeleteFptKey()
    {
        var credentials = Load();
        if (credentials.Remove(FptProvider))
        {
            Save(credentials);
        }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_credentialPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(_credentialPath);
            var plainBytes = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(plainBytes, JsonOptions);
            return values is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            DeleteCorruptedFile();
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save(Dictionary<string, string> credentials)
    {
        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(credentials, JsonOptions);
        var protectedBytes = ProtectedData.Protect(
            plainBytes,
            Entropy,
            DataProtectionScope.CurrentUser);
        var temporaryPath = _credentialPath + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, _credentialPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void DeleteCorruptedFile()
    {
        if (File.Exists(_credentialPath))
        {
            File.Delete(_credentialPath);
        }
    }
}
