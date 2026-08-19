using System.Text.Json;
using SubVid.App.Core;

namespace SubVid.App.LocalAi;

internal static class VoiceInstallLog
{
    private static readonly object Sync = new();

    public static void Write(
        AppPaths paths,
        string engine,
        string? voiceId,
        string stage,
        string message,
        Exception? exception = null)
    {
        try
        {
            var errorCode = exception is LocalModelException modelException
                ? modelException.Code
                : null;
            var errorDetail = exception?.ToString();
            if (errorDetail is { Length: > 4_000 })
            {
                errorDetail = errorDetail[..4_000];
            }

            var line = JsonSerializer.Serialize(new
            {
                timestampUtc = DateTime.UtcNow,
                engine,
                voiceId,
                stage,
                message,
                errorCode,
                errorDetail,
                aiRootPath = paths.AiRootDirectory,
            });
            lock (Sync)
            {
                Directory.CreateDirectory(paths.LogsDirectory);
                File.AppendAllText(
                    Path.Combine(paths.LogsDirectory, "voice-install.jsonl"),
                    line + Environment.NewLine);
            }
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
            // Logging must never prevent installation or recovery of a local voice.
        }
    }
}
