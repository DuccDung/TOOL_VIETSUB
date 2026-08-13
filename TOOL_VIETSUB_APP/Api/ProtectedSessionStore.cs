using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TOOL_VIETSUB_APP.Api;

public sealed class ProtectedSessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TOOL_VIETSUB_APP_AUTH_V1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _sessionPath;

    public ProtectedSessionStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TOOL_VIETSUB");
        Directory.CreateDirectory(directory);
        _sessionPath = Path.Combine(directory, "auth.session");
    }

    public StoredAuthSession? Load()
    {
        if (!File.Exists(_sessionPath))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(_sessionPath);
            var plainBytes = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredAuthSession>(plainBytes, JsonOptions);
        }
        catch (CryptographicException)
        {
            Delete();
            return null;
        }
        catch (JsonException)
        {
            Delete();
            return null;
        }
    }

    public void Save(StoredAuthSession session)
    {
        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        var protectedBytes = ProtectedData.Protect(
            plainBytes,
            Entropy,
            DataProtectionScope.CurrentUser);
        var temporaryPath = _sessionPath + ".tmp";
        File.WriteAllBytes(temporaryPath, protectedBytes);
        File.Move(temporaryPath, _sessionPath, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(_sessionPath))
        {
            File.Delete(_sessionPath);
        }
    }
}
