using System.Collections.Concurrent;
using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Domain.Models;
using BilibiliDownloader.WinForms.Controls;
using BilibiliDownloader.WinForms.Presentation;
using Microsoft.Extensions.Logging;

namespace BilibiliDownloader.WinForms.Forms;

public partial class MainForm : Form
{
    private readonly IBilibiliService _bilibiliService;
    private readonly IDownloadManager _downloadManager;
    private readonly ISettingsService _settingsService;
    private readonly IFFmpegProvisioningService _ffmpegProvisioningService;
    private readonly IThumbnailService _thumbnailService;
    private readonly IQualitySelectionService _qualitySelectionService;
    private readonly IFileService _fileService;
    private readonly IWindowService _windowService;
    private readonly ILogger<MainForm> _logger;
    private readonly ConcurrentDictionary<Guid, DownloadItemControl> _downloadControls = new();
    private CancellationTokenSource? _analyzeCancellation;
    private readonly CancellationTokenSource _ffmpegProvisioningCancellation = new();
    private BilibiliVideoDto? _currentVideo;
    private bool _isAnalyzing;

    public MainForm(
        IBilibiliService bilibiliService,
        IDownloadManager downloadManager,
        ISettingsService settingsService,
        IFFmpegProvisioningService ffmpegProvisioningService,
        IThumbnailService thumbnailService,
        IQualitySelectionService qualitySelectionService,
        IFileService fileService,
        IWindowService windowService,
        ILogger<MainForm> logger)
    {
        _bilibiliService = bilibiliService;
        _downloadManager = downloadManager;
        _settingsService = settingsService;
        _ffmpegProvisioningService = ffmpegProvisioningService;
        _thumbnailService = thumbnailService;
        _qualitySelectionService = qualitySelectionService;
        _fileService = fileService;
        _windowService = windowService;
        _logger = logger;
        InitializeComponent();
        WireEvents();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            var settings = await _settingsService.GetAsync().ConfigureAwait(true);
            _outputFolderTextBox.Text = settings.DownloadFolder;
            foreach (var job in _downloadManager.GetJobs())
            {
                UpdateDownloadItem(job);
            }

            _urlTextBox.Focus();
            await PrepareFfmpegAsync(_ffmpegProvisioningCancellation.Token).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to initialize MainForm");
            ErrorDialog.Show(this, exception, "Không thể khởi tạo giao diện.");
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _analyzeCancellation?.Cancel();
        _analyzeCancellation?.Dispose();
        _ffmpegProvisioningCancellation.Cancel();
        _ffmpegProvisioningCancellation.Dispose();
        _downloadManager.JobChanged -= DownloadManagerJobChanged;
        base.OnFormClosed(e);
    }

    private void WireEvents()
    {
        _analyzeButton.Click += AnalyzeButtonClick;
        _urlTextBox.KeyDown += UrlTextBoxKeyDown;
        _downloadButton.Click += DownloadButtonClick;
        _browseOutputButton.Click += BrowseOutputButtonClick;
        _settingsButton.Click += async (_, _) => await OpenSettingsAsync().ConfigureAwait(true);
        _historyButton.Click += async (_, _) => await _windowService.ShowHistoryAsync(this).ConfigureAwait(true);
        _aboutButton.Click += (_, _) => _windowService.ShowAbout(this);
        _downloadManager.JobChanged += DownloadManagerJobChanged;
    }

    private async void AnalyzeButtonClick(object? sender, EventArgs e)
    {
        if (_isAnalyzing)
        {
            _analyzeCancellation?.Cancel();
            return;
        }

        await AnalyzeAsync().ConfigureAwait(true);
    }

    private async void UrlTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !_isAnalyzing)
        {
            e.SuppressKeyPress = true;
            await AnalyzeAsync().ConfigureAwait(true);
        }
    }

    private async Task AnalyzeAsync()
    {
        _analyzeCancellation?.Dispose();
        _analyzeCancellation = new CancellationTokenSource();
        SetAnalyzeBusy(true);
        try
        {
            var video = await _bilibiliService
                .AnalyzeAsync(_urlTextBox.Text, _analyzeCancellation.Token)
                .ConfigureAwait(true);
            Image? thumbnail = null;
            if (!string.IsNullOrWhiteSpace(video.ThumbnailUrl))
            {
                try
                {
                    var bytes = await _thumbnailService
                        .DownloadAsync(video.ThumbnailUrl, _analyzeCancellation.Token)
                        .ConfigureAwait(true);
                    using var stream = new MemoryStream(bytes);
                    using var loaded = Image.FromStream(stream);
                    thumbnail = new Bitmap(loaded);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "Unable to load thumbnail for {VideoId}", video.Id);
                }
            }

            _currentVideo = video;
            var settings = await _settingsService.GetAsync(_analyzeCancellation.Token).ConfigureAwait(true);
            var preferred = _qualitySelectionService.SelectBest(video.Streams, settings.DefaultQuality);
            _videoInfo.ShowVideo(video, thumbnail);
            _qualitySelector.BindStreams(video.Streams, preferred);
            _outputFolderTextBox.Text = settings.DownloadFolder;
            _videoCard.Visible = true;
            _statusLabel.Text = $"Đã phân tích • {video.Streams.Count} chất lượng";

            if (settings.StartDownloadAutomatically)
            {
                await EnqueueCurrentVideoAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Đã hủy phân tích";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Analyze failed");
            ErrorDialog.Show(this, exception, "Không thể phân tích video.");
            _statusLabel.Text = "Phân tích thất bại";
        }
        finally
        {
            SetAnalyzeBusy(false);
        }
    }

    private async void DownloadButtonClick(object? sender, EventArgs e) =>
        await EnqueueCurrentVideoAsync().ConfigureAwait(true);

    private async Task EnqueueCurrentVideoAsync()
    {
        if (_currentVideo is null || _qualitySelector.SelectedStream is null)
        {
            MessageBox.Show(this, "Hãy phân tích và chọn chất lượng video trước.", Text);
            return;
        }

        try
        {
            _downloadButton.Enabled = false;
            var request = new DownloadRequestDto
            {
                VideoId = _currentVideo.Id,
                SourceUrl = _currentVideo.SourceUrl,
                Title = _currentVideo.Title,
                Author = _currentVideo.Author,
                ThumbnailUrl = _currentVideo.ThumbnailUrl,
                Stream = _qualitySelector.SelectedStream,
                OutputDirectory = _outputFolderTextBox.Text,
                Format = "MP4"
            };
            await _downloadManager.EnqueueAsync(request).ConfigureAwait(true);
            _statusLabel.Text = "Đã thêm vào hàng đợi";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to enqueue download");
            ErrorDialog.Show(this, exception, "Không thể thêm download vào hàng đợi.");
        }
        finally
        {
            _downloadButton.Enabled = true;
        }
    }

    private void BrowseOutputButtonClick(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Chọn thư mục lưu video",
            UseDescriptionForTitle = true,
            SelectedPath = _outputFolderTextBox.Text,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputFolderTextBox.Text = dialog.SelectedPath;
        }
    }

    private async Task OpenSettingsAsync()
    {
        await _windowService.ShowSettingsAsync(this).ConfigureAwait(true);
        var settings = await _settingsService.GetAsync().ConfigureAwait(true);
        _outputFolderTextBox.Text = settings.DownloadFolder;
    }

    private void DownloadManagerJobChanged(object? sender, DownloadJobSnapshot snapshot)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateDownloadItem(snapshot));
        }
        else
        {
            UpdateDownloadItem(snapshot);
        }
    }

    private void UpdateDownloadItem(DownloadJobSnapshot snapshot)
    {
        var control = _downloadControls.GetOrAdd(snapshot.Id, _ =>
        {
            var item = new DownloadItemControl();
            item.CancelRequested += (_, jobId) => _downloadManager.Cancel(jobId);
            item.OpenFolderRequested += (_, path) =>
            {
                try
                {
                    _fileService.OpenFolder(path);
                }
                catch (Exception exception)
                {
                    ErrorDialog.Show(this, exception, "Không thể mở thư mục.");
                }
            };
            _downloadQueue.Controls.Add(item);
            ResizeQueueItems();
            return item;
        });
        control.UpdateJob(snapshot);
        _statusLabel.Text = snapshot.Status switch
        {
            DownloadStatus.Completed => "Download hoàn tất",
            DownloadStatus.Failed => "Có download thất bại",
            DownloadStatus.Cancelled => "Đã hủy download",
            _ => "Đang xử lý hàng đợi"
        };
    }

    private void SetAnalyzeBusy(bool busy)
    {
        _isAnalyzing = busy;
        _analyzeButton.Text = busy ? "HỦY PHÂN TÍCH" : "PHÂN TÍCH VIDEO";
        _urlTextBox.Enabled = !busy;
        _downloadButton.Enabled = !busy;
        _statusLabel.Text = busy ? "Đang phân tích video..." : _statusLabel.Text;
    }

    private async Task PrepareFfmpegAsync(CancellationToken cancellationToken)
    {
        var progress = new Progress<FFmpegProvisioningProgressDto>(value =>
        {
            if (IsDisposed)
            {
                return;
            }

            _statusLabel.Text = value.Message;
            _statusLabel.ForeColor = value.State switch
            {
                FFmpegProvisioningState.Ready => UiTheme.Success,
                FFmpegProvisioningState.Failed => UiTheme.Danger,
                _ => UiTheme.MutedText
            };
        });

        try
        {
            await _ffmpegProvisioningService
                .EnsureAvailableAsync(progress, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application is closing.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Automatic FFmpeg provisioning was not completed");
            _statusLabel.Text = "Chưa thể chuẩn bị FFmpeg • mở Cài đặt để thử lại";
            _statusLabel.ForeColor = UiTheme.Danger;
        }
    }
}
