using System.Collections.Concurrent;
using System.Threading.Channels;
using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Domain.Models;
using BilibiliDownloader.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BilibiliDownloader.Infrastructure.Download;

public sealed class DownloadManager : BackgroundService, IDownloadManager
{
    private readonly Channel<Guid> _queue;
    private readonly ConcurrentDictionary<Guid, JobRuntime> _jobs = new();
    private readonly IBilibiliService _bilibiliService;
    private readonly IDownloadService _downloadService;
    private readonly IQualitySelectionService _qualitySelectionService;
    private readonly IFileService _fileService;
    private readonly ISettingsService _settingsService;
    private readonly IHistoryService _historyService;
    private readonly ILogger<DownloadManager> _logger;
    private int _maximumConcurrentDownloads = 2;

    public DownloadManager(
        IBilibiliService bilibiliService,
        IDownloadService downloadService,
        IQualitySelectionService qualitySelectionService,
        IFileService fileService,
        ISettingsService settingsService,
        IHistoryService historyService,
        IOptions<DownloadOptions> options,
        ILogger<DownloadManager> logger)
    {
        _bilibiliService = bilibiliService;
        _downloadService = downloadService;
        _qualitySelectionService = qualitySelectionService;
        _fileService = fileService;
        _settingsService = settingsService;
        _historyService = historyService;
        _logger = logger;
        _queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _settingsService.SettingsChanged += SettingsChanged;
    }

    public event EventHandler<DownloadJobSnapshot>? JobChanged;

    public async ValueTask<Guid> EnqueueAsync(
        DownloadRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = request.JobId == Guid.Empty ? Guid.NewGuid() : request.JobId;
        var normalizedRequest = request with { JobId = id };
        var runtime = new JobRuntime(normalizedRequest);
        if (!_jobs.TryAdd(id, runtime))
        {
            throw new AppException(AppErrorCode.DownloadError, "Download job đã tồn tại.");
        }

        try
        {
            await _historyService.AddAsync(new DownloadHistory
            {
                Id = id,
                VideoId = request.VideoId,
                SourceUrl = request.SourceUrl,
                Title = request.Title,
                Quality = request.Stream.Quality,
                Format = request.Format,
                Status = DownloadStatus.Queued,
                CreatedAtUtc = DateTime.UtcNow
            }, cancellationToken).ConfigureAwait(false);
            await _queue.Writer.WriteAsync(id, cancellationToken).ConfigureAwait(false);
            Publish(runtime);
            _logger.LogInformation("Queued download job {JobId} for video {VideoId}", id, request.VideoId);
            return id;
        }
        catch
        {
            _jobs.TryRemove(id, out _);
            runtime.Dispose();
            throw;
        }
    }

    public bool Cancel(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var runtime) || runtime.IsTerminal)
        {
            return false;
        }

        runtime.Cancel();
        runtime.Update(DownloadStatus.Cancelled, DownloadStage.Cancelled, errorMessage: "Đã hủy bởi người dùng.");
        Publish(runtime);
        return true;
    }

    public IReadOnlyList<DownloadJobSnapshot> GetJobs() => _jobs.Values
        .Select(runtime => runtime.Snapshot())
        .OrderBy(snapshot => snapshot.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled)
        .ThenBy(snapshot => snapshot.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = await _settingsService.GetAsync(stoppingToken).ConfigureAwait(false);
        Volatile.Write(ref _maximumConcurrentDownloads, settings.MaximumConcurrentDownloads);
        var active = new Dictionary<Guid, Task>();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                RemoveCompleted(active);
                var limit = Volatile.Read(ref _maximumConcurrentDownloads);
                while (active.Count < limit && _queue.Reader.TryRead(out var jobId))
                {
                    if (!_jobs.TryGetValue(jobId, out var runtime))
                    {
                        continue;
                    }

                    active[jobId] = RunJobAsync(runtime, stoppingToken);
                }

                if (active.Count == 0)
                {
                    if (!await _queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                else if (active.Count >= limit)
                {
                    await Task.WhenAny(active.Values).ConfigureAwait(false);
                }
                else
                {
                    var queueReady = _queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
                    var jobCompleted = Task.WhenAny(active.Values);
                    await Task.WhenAny(queueReady, jobCompleted).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        finally
        {
            foreach (var runtime in _jobs.Values.Where(runtime => !runtime.IsTerminal))
            {
                runtime.Cancel();
            }

            try
            {
                await Task.WhenAll(active.Values).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogDebug(exception, "A job ended while the download manager was stopping");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        foreach (var runtime in _jobs.Values.Where(runtime => !runtime.IsTerminal))
        {
            runtime.Cancel();
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _settingsService.SettingsChanged -= SettingsChanged;
        foreach (var runtime in _jobs.Values)
        {
            runtime.Dispose();
        }

        base.Dispose();
    }

    private async Task RunJobAsync(JobRuntime runtime, CancellationToken stoppingToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            runtime.CancellationToken,
            stoppingToken);
        var cancellationToken = linkedCancellation.Token;
        try
        {
            if (runtime.IsCancellationRequested)
            {
                await MarkCancelledAsync(runtime, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            runtime.Update(DownloadStatus.Resolving, DownloadStage.Resolving);
            Publish(runtime);
            await _historyService.UpdateStatusAsync(
                runtime.Id,
                DownloadStatus.Resolving,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var video = await _bilibiliService
                .AnalyzeAsync(runtime.Request.SourceUrl, cancellationToken)
                .ConfigureAwait(false);
            var selectedStream = video.Streams.FirstOrDefault(stream => stream.Id == runtime.Request.Stream.Id) ??
                video.Streams.FirstOrDefault(stream => stream.QualityId == runtime.Request.Stream.QualityId) ??
                _qualitySelectionService.SelectBest(video.Streams, HeightToQuality(runtime.Request.Stream.Height));
            var outputPath = _fileService.CreateUniqueOutputPath(
                runtime.Request.OutputDirectory,
                runtime.Request.Title,
                runtime.Request.Format);
            runtime.SetOutputPath(outputPath);
            var resolvedRequest = runtime.Request with
            {
                Stream = selectedStream,
                OutputPath = outputPath
            };

            runtime.Update(DownloadStatus.Downloading, DownloadStage.DownloadingVideo);
            Publish(runtime);
            await _historyService.UpdateStatusAsync(
                runtime.Id,
                DownloadStatus.Downloading,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var progress = new InlineProgress<DownloadProgressDto>(value =>
            {
                runtime.ApplyProgress(value);
                Publish(runtime);
            });
            await _downloadService.DownloadAsync(resolvedRequest, progress, cancellationToken).ConfigureAwait(false);

            var fileSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
            runtime.Update(DownloadStatus.Completed, DownloadStage.Completed, percentage: 100);
            Publish(runtime);
            await _historyService.UpdateStatusAsync(
                runtime.Id,
                DownloadStatus.Completed,
                outputPath,
                fileSize,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            var settings = await _settingsService.GetAsync(CancellationToken.None).ConfigureAwait(false);
            if (settings.AutoOpenFolder)
            {
                _fileService.OpenFolder(outputPath);
            }
        }
        catch (OperationCanceledException)
        {
            await MarkCancelledAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AppException exception)
        {
            runtime.Update(DownloadStatus.Failed, DownloadStage.Failed, errorMessage: exception.Message);
            Publish(runtime);
            await _historyService.UpdateStatusAsync(
                runtime.Id,
                DownloadStatus.Failed,
                errorCode: exception.PublicCode,
                errorMessage: exception.Message,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            _logger.LogError(
                "Download job {JobId} failed with {ErrorCode}: {ErrorMessage}",
                runtime.Id,
                exception.PublicCode,
                exception.Message);
        }
        catch (Exception exception)
        {
            const string message = "Đã xảy ra lỗi không xác định khi tải video.";
            runtime.Update(DownloadStatus.Failed, DownloadStage.Failed, errorMessage: message);
            Publish(runtime);
            await _historyService.UpdateStatusAsync(
                runtime.Id,
                DownloadStatus.Failed,
                errorCode: "UNKNOWN_ERROR",
                errorMessage: message,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            _logger.LogError(exception, "Unexpected error in download job {JobId}", runtime.Id);
        }
    }

    private async Task MarkCancelledAsync(JobRuntime runtime, CancellationToken cancellationToken)
    {
        runtime.Update(DownloadStatus.Cancelled, DownloadStage.Cancelled, errorMessage: "Đã hủy bởi người dùng.");
        Publish(runtime);
        await _historyService.UpdateStatusAsync(
            runtime.Id,
            DownloadStatus.Cancelled,
            errorCode: "CANCELLED",
            errorMessage: "Đã hủy bởi người dùng.",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void Publish(JobRuntime runtime)
    {
        var snapshot = runtime.Snapshot();
        var handlers = JobChanged?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.Cast<EventHandler<DownloadJobSnapshot>>())
        {
            try
            {
                handler(this, snapshot);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogWarning(exception, "A download job UI observer failed");
            }
        }
    }

    private static void RemoveCompleted(Dictionary<Guid, Task> active)
    {
        foreach (var jobId in active.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToArray())
        {
            active.Remove(jobId);
        }
    }

    private void SettingsChanged(object? sender, AppSettings settings) =>
        Volatile.Write(ref _maximumConcurrentDownloads, Math.Clamp(settings.MaximumConcurrentDownloads, 1, 4));

    private static VideoQuality HeightToQuality(int height) => height switch
    {
        >= 4320 => VideoQuality.P4320,
        >= 2160 => VideoQuality.P2160,
        >= 1440 => VideoQuality.P1440,
        >= 1080 => VideoQuality.P1080,
        >= 720 => VideoQuality.P720,
        >= 480 => VideoQuality.P480,
        >= 360 => VideoQuality.P360,
        _ => VideoQuality.P240
    };

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private sealed class JobRuntime(DownloadRequestDto request) : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private DownloadStatus _status = DownloadStatus.Queued;
        private DownloadStage _stage = DownloadStage.Waiting;
        private long _downloadedBytes;
        private long? _totalBytes;
        private double _percentage;
        private double _speed;
        private TimeSpan? _remaining;
        private string? _outputPath;
        private string? _errorMessage;

        public Guid Id => Request.JobId;
        public DownloadRequestDto Request { get; } = request;
        public CancellationToken CancellationToken => _cancellation.Token;
        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        public bool IsTerminal
        {
            get
            {
                lock (_gate)
                {
                    return _status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled;
                }
            }
        }

        public void Cancel() => _cancellation.Cancel();

        public void SetOutputPath(string outputPath)
        {
            lock (_gate)
            {
                _outputPath = outputPath;
            }
        }

        public void ApplyProgress(DownloadProgressDto progress)
        {
            lock (_gate)
            {
                _stage = progress.Stage;
                _status = progress.Stage switch
                {
                    DownloadStage.Resolving => DownloadStatus.Resolving,
                    DownloadStage.Merging => DownloadStatus.Merging,
                    DownloadStage.Completed => DownloadStatus.Completed,
                    _ => DownloadStatus.Downloading
                };
                _downloadedBytes = progress.DownloadedBytes;
                _totalBytes = progress.TotalBytes;
                _percentage = progress.Percentage;
                _speed = progress.SpeedBytesPerSecond;
                _remaining = progress.RemainingTime;
            }
        }

        public void Update(
            DownloadStatus status,
            DownloadStage stage,
            double? percentage = null,
            string? errorMessage = null)
        {
            lock (_gate)
            {
                _status = status;
                _stage = stage;
                _percentage = percentage ?? _percentage;
                _errorMessage = errorMessage;
                if (status is DownloadStatus.Cancelled or DownloadStatus.Failed)
                {
                    _speed = 0;
                    _remaining = null;
                }
            }
        }

        public DownloadJobSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new DownloadJobSnapshot(
                    Id,
                    Request.VideoId,
                    Request.Title,
                    Request.Stream.Quality,
                    Request.Format,
                    _status,
                    _stage,
                    _downloadedBytes,
                    _totalBytes,
                    _percentage,
                    _speed,
                    _remaining,
                    _outputPath,
                    _errorMessage);
            }
        }

        public void Dispose() => _cancellation.Dispose();
    }
}
