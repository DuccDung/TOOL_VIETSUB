using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.WinForms.Presentation;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace BilibiliDownloader.WinForms.Forms;

public sealed class HistoryForm : Form
{
    private readonly IHistoryService _historyService;
    private readonly IFileService _fileService;
    private readonly IBilibiliService _bilibiliService;
    private readonly IDownloadManager _downloadManager;
    private readonly IQualitySelectionService _qualitySelectionService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<HistoryForm> _logger;
    private readonly DataGridView _grid;
    private readonly Label _status;

    public HistoryForm(
        IHistoryService historyService,
        IFileService fileService,
        IBilibiliService bilibiliService,
        IDownloadManager downloadManager,
        IQualitySelectionService qualitySelectionService,
        ISettingsService settingsService,
        ILogger<HistoryForm> logger)
    {
        _historyService = historyService;
        _fileService = fileService;
        _bilibiliService = bilibiliService;
        _downloadManager = downloadManager;
        _qualitySelectionService = qualitySelectionService;
        _settingsService = settingsService;
        _logger = logger;
        Text = "Lịch sử tải xuống";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(820, 500);
        ClientSize = new Size(980, 620);
        BackColor = UiTheme.Background;
        Padding = new Padding(20);
        Font = new Font("Segoe UI", 9F);

        var header = UiTheme.CreateLabel("Lịch sử tải xuống", 16F, FontStyle.Bold);
        header.ForeColor = UiTheme.PrimaryDark;
        header.Dock = DockStyle.Top;
        header.Height = 46;
        _status = UiTheme.CreateLabel("", 8.5F);
        _status.ForeColor = UiTheme.MutedText;
        _status.Dock = DockStyle.Bottom;
        _status.Height = 26;

        _grid = CreateGrid();
        var footer = CreateFooter();
        Controls.Add(_grid);
        Controls.Add(footer);
        Controls.Add(_status);
        Controls.Add(header);
        Shown += async (_, _) => await LoadHistoryAsync().ConfigureAwait(true);
    }

    private static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = UiTheme.Surface,
            BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            MultiSelect = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            ColumnHeadersHeight = 38,
            RowTemplate = { Height = 34 }
        };
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(234, 240, 247);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Text;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 244, 252);
        grid.DefaultCellStyle.SelectionForeColor = UiTheme.Text;
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(HistoryRow.Title),
            HeaderText = "Tiêu đề",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 45
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(HistoryRow.Quality),
            HeaderText = "Chất lượng",
            Width = 95
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(HistoryRow.CreatedAt),
            HeaderText = "Ngày",
            Width = 135
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(HistoryRow.Status),
            HeaderText = "Trạng thái",
            Width = 105
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(HistoryRow.FileSize),
            HeaderText = "Dung lượng",
            Width = 105
        });
        return grid;
    }

    private Control CreateFooter()
    {
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0)
        };
        var refresh = UiTheme.CreateSecondaryButton("Làm mới");
        var openFile = UiTheme.CreateSecondaryButton("Mở file");
        var openFolder = UiTheme.CreateSecondaryButton("Mở thư mục");
        var redownload = UiTheme.CreatePrimaryButton("Tải lại");
        var delete = UiTheme.CreateSecondaryButton("Xóa lịch sử");
        delete.ForeColor = UiTheme.Danger;
        refresh.Click += async (_, _) => await LoadHistoryAsync().ConfigureAwait(true);
        openFile.Click += (_, _) => WithSelection(item => _fileService.OpenFile(item.FilePath!));
        openFolder.Click += (_, _) => WithSelection(item => _fileService.OpenFolder(item.FilePath!));
        delete.Click += async (_, _) => await DeleteSelectedAsync().ConfigureAwait(true);
        redownload.Click += async (_, _) => await RedownloadSelectedAsync().ConfigureAwait(true);
        footer.Controls.Add(refresh);
        footer.Controls.Add(openFile);
        footer.Controls.Add(openFolder);
        footer.Controls.Add(redownload);
        footer.Controls.Add(delete);
        return footer;
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            _status.Text = "Đang tải...";
            var entries = await _historyService.GetRecentAsync().ConfigureAwait(true);
            _grid.DataSource = entries.Select(item => new HistoryRow(item)).ToArray();
            _status.Text = $"{entries.Count} mục gần nhất";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to load download history");
            ErrorDialog.Show(this, exception, "Không thể tải lịch sử.");
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var selected = Selected;
        if (selected is null)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                "Chỉ xóa bản ghi lịch sử, không xóa file đã tải. Tiếp tục?",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            await _historyService.DeleteAsync(selected.Source.Id).ConfigureAwait(true);
            await LoadHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ErrorDialog.Show(this, exception, "Không thể xóa lịch sử.");
        }
    }

    private async Task RedownloadSelectedAsync()
    {
        var selected = Selected;
        if (selected is null)
        {
            return;
        }

        try
        {
            _status.Text = "Đang phân tích lại video...";
            var video = await _bilibiliService.AnalyzeAsync(selected.Source.SourceUrl, CancellationToken.None).ConfigureAwait(true);
            var settings = await _settingsService.GetAsync().ConfigureAwait(true);
            var stream = video.Streams.FirstOrDefault(item => item.Quality == selected.Source.Quality) ??
                _qualitySelectionService.SelectBest(video.Streams, settings.DefaultQuality);
            await _downloadManager.EnqueueAsync(new DownloadRequestDto
            {
                VideoId = video.Id,
                SourceUrl = video.SourceUrl,
                Title = video.Title,
                Author = video.Author,
                ThumbnailUrl = video.ThumbnailUrl,
                Stream = stream,
                OutputDirectory = settings.DownloadFolder,
                Format = "MP4"
            }).ConfigureAwait(true);
            _status.Text = "Đã thêm lại vào hàng đợi";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to redownload history item {HistoryId}", selected.Source.Id);
            ErrorDialog.Show(this, exception, "Không thể tải lại video.");
        }
    }

    private void WithSelection(Action<DownloadHistory> action)
    {
        var selected = Selected;
        if (selected is null || string.IsNullOrWhiteSpace(selected.Source.FilePath))
        {
            MessageBox.Show(this, "Mục được chọn không có file hợp lệ.", Text);
            return;
        }

        try
        {
            action(selected.Source);
        }
        catch (Exception exception)
        {
            ErrorDialog.Show(this, exception, "Không thể mở file hoặc thư mục.");
        }
    }

    private HistoryRow? Selected => _grid.CurrentRow?.DataBoundItem as HistoryRow;

    private sealed record HistoryRow(DownloadHistory Source)
    {
        public string Title => Source.Title;
        public string Quality => Source.Quality;
        public string CreatedAt => Source.CreatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
        public string Status => Source.Status switch
        {
            DownloadStatus.Completed => "Hoàn tất",
            DownloadStatus.Failed => "Thất bại",
            DownloadStatus.Cancelled => "Đã hủy",
            DownloadStatus.Interrupted => "Gián đoạn",
            _ => Source.Status.ToString()
        };
        public string FileSize => Source.FileSize is > 0 ? Formatters.Bytes(Source.FileSize.Value) : "—";
    }
}
