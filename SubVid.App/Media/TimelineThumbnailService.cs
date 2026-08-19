using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SubVid.App.Core;

namespace SubVid.App.Media;

internal sealed record TimelineThumbnailReady(
    string SourceSha256,
    int Index,
    string Url);

internal sealed record TimelineThumbnailFailed(
    string SourceSha256,
    int Index);

internal sealed class TimelineThumbnailService : IAsyncDisposable
{
    public const int ProfileVersion = 1;
    public const int ThumbnailCount = 160;
    public const int ThumbnailWidth = 192;
    public const int ThumbnailHeight = 108;

    private const string MediaHostName = "media.subvid.local";
    private readonly AppPaths _paths;
    private readonly Func<string?> _resolveFfmpegPath;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly object _sync = new();
    private readonly LinkedList<ThumbnailWorkItem> _queue = [];
    private readonly HashSet<string> _pendingKeys = new(StringComparer.Ordinal);
    private readonly Task _worker;
    private ActiveSource? _activeSource;
    private CancellationTokenSource? _activeGenerationCancellation;
    private bool _resourcePaused;
    private bool _disposed;

    public TimelineThumbnailService(AppPaths paths, Func<string?> resolveFfmpegPath)
    {
        _paths = paths;
        _resolveFfmpegPath = resolveFfmpegPath;
        _worker = Task.Run(ProcessQueueAsync);
    }

    public event EventHandler<TimelineThumbnailReady>? ThumbnailReady;

    public event EventHandler<TimelineThumbnailFailed>? ThumbnailFailed;

    public void Request(
        Guid projectId,
        string sourcePath,
        string sourceSha256,
        double durationSeconds,
        IReadOnlyList<int> indices)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedSha256 = NormalizeSha256(sourceSha256);
        var resolvedSourcePath = Path.GetFullPath(sourcePath);
        var ffmpegPath = _resolveFfmpegPath();
        var canGenerate = !string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath);
        if (!File.Exists(resolvedSourcePath)
            || !double.IsFinite(durationSeconds)
            || durationSeconds <= 0)
        {
            return;
        }

        var source = new ActiveSource(
            projectId,
            resolvedSourcePath,
            normalizedSha256,
            durationSeconds);
        var cached = new List<TimelineThumbnailReady>();
        var prioritized = new List<ThumbnailWorkItem>();
        var queuedBackgroundWork = false;

        lock (_sync)
        {
            var sourceChanged = _activeSource is null || !_activeSource.HasSameIdentity(source);
            if (sourceChanged)
            {
                _activeSource = source;
                _queue.Clear();
                _pendingKeys.Clear();
                _activeGenerationCancellation?.Cancel();
            }

            foreach (var index in indices.Distinct())
            {
                if (index is < 0 or >= ThumbnailCount) continue;
                var outputPath = GetCachePath(normalizedSha256, index);
                if (IsUsableThumbnail(outputPath))
                {
                    cached.Add(new TimelineThumbnailReady(
                        normalizedSha256,
                        index,
                        GetThumbnailUrl(normalizedSha256, index)));
                    continue;
                }
                if (!canGenerate) continue;

                var pendingKey = GetPendingKey(normalizedSha256, index);
                if (_pendingKeys.Contains(pendingKey))
                {
                    var queuedNode = FindQueuedNode(pendingKey);
                    if (queuedNode is not null)
                    {
                        _queue.Remove(queuedNode);
                        prioritized.Add(queuedNode.Value);
                    }
                    continue;
                }

                _pendingKeys.Add(pendingKey);
                prioritized.Add(new ThumbnailWorkItem(source, index, pendingKey, outputPath));
            }

            for (var index = prioritized.Count - 1; index >= 0; index--)
            {
                _queue.AddFirst(prioritized[index]);
            }

            if (sourceChanged && canGenerate)
            {
                foreach (var index in GetBackgroundWarmOrder())
                {
                    var outputPath = GetCachePath(normalizedSha256, index);
                    var pendingKey = GetPendingKey(normalizedSha256, index);
                    if (IsUsableThumbnail(outputPath) || _pendingKeys.Contains(pendingKey)) continue;
                    _pendingKeys.Add(pendingKey);
                    _queue.AddLast(new ThumbnailWorkItem(source, index, pendingKey, outputPath));
                    queuedBackgroundWork = true;
                }
            }
        }

        foreach (var thumbnail in cached)
        {
            ThumbnailReady?.Invoke(this, thumbnail);
        }

        if (prioritized.Count > 0 || queuedBackgroundWork) _queueSignal.Release();
    }

    public void SetResourcePaused(bool paused)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_resourcePaused == paused) return;
            _resourcePaused = paused;
            if (paused) _activeGenerationCancellation?.Cancel();
        }

        if (!paused) _queueSignal.Release();
    }

    public string? TryResolveCachedPath(string absolutePath)
    {
        if (!TryParseThumbnailPath(absolutePath, out var sourceSha256, out var index))
        {
            return null;
        }

        var path = GetCachePath(sourceSha256, index);
        return IsUsableThumbnail(path) ? path : null;
    }

    internal string GetCachePath(string sourceSha256, int index)
    {
        var normalizedSha256 = NormalizeSha256(sourceSha256);
        if (index is < 0 or >= ThumbnailCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _paths.GetCachePath(
            "timeline-thumbnails",
            $"v{ProfileVersion}",
            normalizedSha256,
            $"{index:D3}.jpg");
    }

    internal static double GetTimestamp(double durationSeconds, int index)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }
        if (index is < 0 or >= ThumbnailCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var midpoint = durationSeconds * (index + 0.5d) / ThumbnailCount;
        return Math.Clamp(midpoint, 0.001d, Math.Max(0.001d, durationSeconds - 0.04d));
    }

    internal static string GetThumbnailUrl(string sourceSha256, int index)
    {
        var normalizedSha256 = NormalizeSha256(sourceSha256);
        if (index is < 0 or >= ThumbnailCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return $"https://{MediaHostName}/thumbnail/v{ProfileVersion}/{normalizedSha256}/{index:D3}.jpg";
    }

    internal static bool TryParseThumbnailPath(
        string absolutePath,
        out string sourceSha256,
        out int index)
    {
        sourceSha256 = string.Empty;
        index = -1;
        var parts = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !string.Equals(parts[0], "thumbnail", StringComparison.Ordinal)
            || !string.Equals(parts[1], $"v{ProfileVersion}", StringComparison.Ordinal)
            || !IsSha256(parts[2])
            || !parts[3].EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(
                parts[3].AsSpan(0, parts[3].Length - ".jpg".Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out index)
            || index is < 0 or >= ThumbnailCount)
        {
            sourceSha256 = string.Empty;
            index = -1;
            return false;
        }

        sourceSha256 = parts[2].ToLowerInvariant();
        return true;
    }

    private async Task ProcessQueueAsync()
    {
        while (!_lifetimeCancellation.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(_lifetimeCancellation.Token);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                break;
            }

            while (TryTakeNext(out var item, out var generationCancellation))
            {
                var keepPending = false;
                try
                {
                    await GenerateAsync(item, generationCancellation.Token);
                    if (IsStillActive(item.Source) && File.Exists(item.OutputPath))
                    {
                        ThumbnailReady?.Invoke(this, new TimelineThumbnailReady(
                            item.Source.SourceSha256,
                            item.Index,
                            GetThumbnailUrl(item.Source.SourceSha256, item.Index)));
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!_lifetimeCancellation.IsCancellationRequested && IsStillActive(item.Source))
                    {
                        keepPending = Requeue(item);
                    }
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException
                    or System.ComponentModel.Win32Exception)
                {
#if DEBUG
                    Debug.WriteLine(exception);
#endif
                    ThumbnailFailed?.Invoke(this, new TimelineThumbnailFailed(
                        item.Source.SourceSha256,
                        item.Index));
                }
                finally
                {
                    generationCancellation.Dispose();
                    lock (_sync)
                    {
                        if (ReferenceEquals(_activeGenerationCancellation, generationCancellation))
                        {
                            _activeGenerationCancellation = null;
                        }
                        if (!keepPending) _pendingKeys.Remove(item.PendingKey);
                    }
                }
            }
        }
    }

    private bool TryTakeNext(
        out ThumbnailWorkItem item,
        out CancellationTokenSource generationCancellation)
    {
        lock (_sync)
        {
            if (_lifetimeCancellation.IsCancellationRequested)
            {
                _queue.Clear();
                _pendingKeys.Clear();
                item = null!;
                generationCancellation = null!;
                return false;
            }
            if (_resourcePaused || _queue.First is null)
            {
                item = null!;
                generationCancellation = null!;
                return false;
            }

            item = _queue.First.Value;
            _queue.RemoveFirst();
            generationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            _activeGenerationCancellation = generationCancellation;
            return true;
        }
    }

    private bool Requeue(ThumbnailWorkItem item)
    {
        lock (_sync)
        {
            if (_lifetimeCancellation.IsCancellationRequested
                || _activeSource is null
                || !_activeSource.HasSameIdentity(item.Source))
            {
                return false;
            }

            _queue.AddFirst(item);
            return true;
        }
    }

    private bool IsStillActive(ActiveSource source)
    {
        lock (_sync)
        {
            return _activeSource?.HasSameIdentity(source) == true;
        }
    }

    private LinkedListNode<ThumbnailWorkItem>? FindQueuedNode(string pendingKey)
    {
        var current = _queue.First;
        while (current is not null)
        {
            if (string.Equals(current.Value.PendingKey, pendingKey, StringComparison.Ordinal))
            {
                return current;
            }
            current = current.Next;
        }
        return null;
    }

    private async Task GenerateAsync(ThumbnailWorkItem item, CancellationToken cancellationToken)
    {
        var ffmpegPath = _resolveFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            throw new InvalidOperationException("FFmpeg is not ready for timeline thumbnails.");
        }

        var outputDirectory = Path.GetDirectoryName(item.OutputPath)
            ?? throw new InvalidOperationException("Timeline thumbnail cache path is invalid.");
        Directory.CreateDirectory(outputDirectory);
        await EnsureProfileAsync(item.Source, outputDirectory, cancellationToken);

        var partialPath = Path.Combine(
            outputDirectory,
            $"{item.Index:D3}.{Guid.NewGuid():N}.partial.jpg");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in BuildArguments(item, partialPath))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start FFmpeg for timeline thumbnails.");
            }
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
#if DEBUG
                Debug.WriteLine(exception);
#endif
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch
            {
                TryKill(process);
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    // The process had already exited before the final wait.
                }
                throw;
            }

            _ = await stdoutTask;
            var error = await stderrTask;
            if (process.ExitCode != 0 || !File.Exists(partialPath))
            {
                throw new InvalidOperationException(
                    "FFmpeg could not create timeline thumbnail. " + GetLastErrorLine(error));
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, item.OutputPath, overwrite: true);
        }
        finally
        {
            TryDelete(partialPath);
        }
    }

    private static IReadOnlyList<string> BuildArguments(
        ThumbnailWorkItem item,
        string partialPath)
    {
        var timestamp = GetTimestamp(item.Source.DurationSeconds, item.Index)
            .ToString("0.###", CultureInfo.InvariantCulture);
        return
        [
            "-hide_banner",
            "-loglevel",
            "error",
            "-ss",
            timestamp,
            "-i",
            item.Source.SourcePath,
            "-map",
            "0:v:0",
            "-frames:v",
            "1",
            "-vf",
            $"scale={ThumbnailWidth}:{ThumbnailHeight}:force_original_aspect_ratio=increase,crop={ThumbnailWidth}:{ThumbnailHeight}",
            "-an",
            "-sn",
            "-dn",
            "-threads",
            "1",
            "-q:v",
            "5",
            "-update",
            "1",
            "-y",
            partialPath,
        ];
    }

    private static async Task EnsureProfileAsync(
        ActiveSource source,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var profilePath = Path.Combine(outputDirectory, "profile.json");
        if (File.Exists(profilePath)) return;

        var partialPath = profilePath + $".{Guid.NewGuid():N}.partial";
        try
        {
            var profile = new
            {
                version = ProfileVersion,
                sourceSha256 = source.SourceSha256,
                durationSeconds = source.DurationSeconds,
                thumbnailCount = ThumbnailCount,
                width = ThumbnailWidth,
                height = ThumbnailHeight,
                createdAtUtc = DateTime.UtcNow,
            };
            await File.WriteAllTextAsync(
                partialPath,
                JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(partialPath, profilePath, overwrite: true);
        }
        finally
        {
            TryDelete(partialPath);
        }
    }

    private static string GetPendingKey(string sourceSha256, int index) =>
        $"{sourceSha256}:{index}";

    private static IEnumerable<int> GetBackgroundWarmOrder()
    {
        const int coarseStep = 5;
        const int coarseOffset = coarseStep / 2;
        for (var index = coarseOffset; index < ThumbnailCount; index += coarseStep)
        {
            yield return index;
        }
        for (var index = 0; index < ThumbnailCount; index++)
        {
            if (index % coarseStep != coarseOffset) yield return index;
        }
    }

    private static bool IsUsableThumbnail(string path)
    {
        try
        {
            return new FileInfo(path) is { Exists: true, Length: >= 128 };
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string NormalizeSha256(string sourceSha256)
    {
        if (!IsSha256(sourceSha256))
        {
            throw new ArgumentException("Source SHA-256 is invalid.", nameof(sourceSha256));
        }
        return sourceSha256.ToLowerInvariant();
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string GetLastErrorLine(string error) =>
        error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "No FFmpeg error details were returned.";

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited while cancellation was being handled.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
#if DEBUG
            Debug.WriteLine(exception);
#endif
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCancellation.Cancel();
        lock (_sync)
        {
            _activeGenerationCancellation?.Cancel();
        }
        _queueSignal.Release();
        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        _queueSignal.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private sealed record ActiveSource(
        Guid ProjectId,
        string SourcePath,
        string SourceSha256,
        double DurationSeconds)
    {
        public bool HasSameIdentity(ActiveSource other) =>
            ProjectId == other.ProjectId
            && string.Equals(SourceSha256, other.SourceSha256, StringComparison.Ordinal)
            && string.Equals(SourcePath, other.SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ThumbnailWorkItem(
        ActiveSource Source,
        int Index,
        string PendingKey,
        string OutputPath);
}
