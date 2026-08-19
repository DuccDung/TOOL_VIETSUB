using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BilibiliDownloader.Infrastructure.FFmpeg;

public interface IFFmpegDiscoveryService
{
    Task<FFmpegProvisioningResultDto?> FindAvailableAsync(CancellationToken cancellationToken);

    Task<FFmpegProvisioningResultDto?> ValidateCandidateAsync(
        string executablePath,
        FFmpegSource source,
        CancellationToken cancellationToken);
}

public sealed class FFmpegDiscoveryService(
    ISettingsService settingsService,
    IFileService fileService,
    IFFmpegProcessRunner processRunner,
    IFFmpegEnvironment environment,
    IOptions<FFmpegOptions> options,
    ILogger<FFmpegDiscoveryService> logger) : IFFmpegDiscoveryService
{
    public async Task<FFmpegProvisioningResultDto?> FindAvailableAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
        var candidates = BuildCandidates(settings.FfmpegPath);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate.Path);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                logger.LogWarning("Ignoring invalid FFmpeg candidate from {Source}", candidate.Source);
                continue;
            }

            if (!visited.Add(fullPath))
            {
                continue;
            }

            var result = await ValidateCandidateAsync(fullPath, candidate.Source, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                logger.LogInformation("Using FFmpeg from {Source}: {Path}", result.Source, result.ExecutablePath);
                return result;
            }
        }

        return null;
    }

    public async Task<FFmpegProvisioningResultDto?> ValidateCandidateAsync(
        string executablePath,
        FFmpegSource source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var run = await processRunner
                .RunAsync(fullPath, ["-hide_banner", "-version"], timeout.Token)
                .ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                logger.LogWarning("FFmpeg candidate from {Source} returned exit code {ExitCode}", source, run.ExitCode);
                return null;
            }

            var firstLine = FirstNonEmptyLine(run.StandardOutput) ?? FirstNonEmptyLine(run.StandardError);
            if (firstLine is null || !firstLine.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Executable from {Source} did not identify itself as FFmpeg", source);
                return null;
            }

            var probePath = Path.Combine(Path.GetDirectoryName(fullPath)!, "ffprobe.exe");
            return new FFmpegProvisioningResultDto
            {
                ExecutablePath = fullPath,
                ProbePath = File.Exists(probePath) ? probePath : null,
                Version = ParseVersion(firstLine),
                Source = source
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Unable to validate FFmpeg candidate from {Source}", source);
            return null;
        }
    }

    private IReadOnlyList<(string Path, FFmpegSource Source)> BuildCandidates(string? configuredPath)
    {
        var candidates = new List<(string Path, FFmpegSource Source)>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add((configuredPath, FFmpegSource.Custom));
        }

        candidates.Add((
            Path.Combine(environment.ApplicationBaseDirectory, "Tools", "ffmpeg", "ffmpeg.exe"),
            FFmpegSource.Bundled));
        candidates.Add((GetManagedExecutablePath(), FFmpegSource.Managed));
        candidates.AddRange(environment.GetPathDirectories().Select(path =>
            (Path.Combine(path, "ffmpeg.exe"), FFmpegSource.System)));
        return candidates;
    }

    private string GetManagedExecutablePath()
    {
        var configured = options.Value;
        return Path.Combine(
            fileService.ToolsDirectory,
            "ffmpeg",
            configured.Version,
            NormalizeRelativePath(configured.FfmpegRelativePath));
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static string? FirstNonEmptyLine(string value) => value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();

    private static string ParseVersion(string firstLine)
    {
        const string prefix = "ffmpeg version";
        var value = firstLine[prefix.Length..].TrimStart();
        var separator = value.IndexOf(' ');
        return separator > 0 ? value[..separator] : value;
    }
}
