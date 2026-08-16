using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Translation;

public sealed class TranslationResultCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly Guid _projectId;

    public TranslationResultCache(AppPaths paths, Guid projectId)
    {
        _paths = paths;
        _projectId = projectId;
    }

    public string BuildKey(
        TranslationSceneRequest request,
        string providerId,
        string modelId,
        bool reviewEnabled)
    {
        var payload = JsonSerializer.Serialize(new
        {
            version = TranslationPromptBuilder.PromptVersion,
            providerId,
            modelId,
            reviewEnabled,
            systemPrompt = TranslationPromptBuilder.SystemPrompt,
            userPrompt = TranslationPromptBuilder.BuildUserPrompt(request),
        }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public async Task<TranslationSceneResult?> TryReadAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var path = GetPath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<TranslationSceneResult>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    public async Task WriteAsync(
        string key,
        TranslationSceneResult result,
        CancellationToken cancellationToken)
    {
        var path = GetPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetPath(string key)
    {
        if (key.Length != 64 || key.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("Khóa cache bản dịch không hợp lệ.", nameof(key));
        }

        return _paths.GetProjectPath(_projectId, "cache", "translation", key + ".json");
    }
}
