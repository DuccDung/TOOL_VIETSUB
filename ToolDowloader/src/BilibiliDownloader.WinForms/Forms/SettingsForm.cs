using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.WinForms.Presentation;
using Microsoft.Extensions.Logging;

namespace BilibiliDownloader.WinForms.Forms;

public sealed class SettingsForm : Form
{
    private readonly ISettingsService _settingsService;
    private readonly IFFmpegService _ffmpegService;
    private readonly IFFmpegProvisioningService _ffmpegProvisioningService;
    private readonly ILogger<SettingsForm> _logger;
    private readonly TextBox _downloadFolder = new();
    private readonly TextBox _ffmpegPath = new();
    private readonly NumericUpDown _concurrency = new();
    private readonly ComboBox _quality = new();
    private readonly NumericUpDown _maxFileSizeGb = new();
    private readonly NumericUpDown _retryCount = new();
    private readonly NumericUpDown _networkTimeout = new();
    private readonly NumericUpDown _ffmpegTimeout = new();
    private readonly CheckBox _autoOpen = new();
    private readonly CheckBox _deleteTemp = new();
    private readonly CheckBox _autoStart = new();
    private readonly Button _saveButton;
    private readonly Button _prepareFfmpegButton;
    private readonly Label _ffmpegStatus;
    private readonly CancellationTokenSource _provisioningCancellation = new();

    public SettingsForm(
        ISettingsService settingsService,
        IFFmpegService ffmpegService,
        IFFmpegProvisioningService ffmpegProvisioningService,
        ILogger<SettingsForm> logger)
    {
        _settingsService = settingsService;
        _ffmpegService = ffmpegService;
        _ffmpegProvisioningService = ffmpegProvisioningService;
        _logger = logger;
        Text = "Cài đặt — Bilibili Downloader";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(670, 650);
        BackColor = UiTheme.Background;
        Font = new Font("Segoe UI", 9F);
        Padding = new Padding(22);

        ConfigureFields();
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 12,
            BackColor = UiTheme.Surface,
            Padding = new Padding(20)
        };
        content.Controls.Add(CreateTitle(), 0, 0);
        content.Controls.Add(CreatePathField("Thư mục tải xuống", _downloadFolder, BrowseFolder), 0, 1);
        content.Controls.Add(CreatePathField("FFmpeg path", _ffmpegPath, BrowseFfmpeg), 0, 2);
        _ffmpegStatus = UiTheme.CreateLabel("Chưa kiểm tra FFmpeg", 8.5F);
        _ffmpegStatus.ForeColor = UiTheme.MutedText;
        content.Controls.Add(_ffmpegStatus, 0, 3);
        content.Controls.Add(CreateTwoColumnFields(), 0, 4);
        content.Controls.Add(CreateLimitsFields(), 0, 5);
        content.Controls.Add(CreateCheckBoxPanel(), 0, 6);
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _saveButton = UiTheme.CreatePrimaryButton("LƯU CÀI ĐẶT");
        _saveButton.Width = 140;
        var cancel = UiTheme.CreateSecondaryButton("Hủy");
        cancel.Width = 90;
        cancel.Click += (_, _) => Close();
        _prepareFfmpegButton = UiTheme.CreateSecondaryButton("Tự động chuẩn bị");
        _prepareFfmpegButton.Width = 150;
        _prepareFfmpegButton.Click += PrepareFfmpegClick;
        _saveButton.Click += SaveClick;
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
        };
        footer.Controls.Add(_saveButton);
        footer.Controls.Add(cancel);
        footer.Controls.Add(_prepareFfmpegButton);
        Controls.Add(content);
        Controls.Add(footer);
        Shown += LoadSettings;
        FormClosed += (_, _) =>
        {
            _provisioningCancellation.Cancel();
            _provisioningCancellation.Dispose();
        };
    }

    private void ConfigureFields()
    {
        foreach (var textBox in new[] { _downloadFolder, _ffmpegPath })
        {
            textBox.Dock = DockStyle.Fill;
            textBox.Font = new Font("Segoe UI", 9.5F);
        }

        _concurrency.Minimum = 1;
        _concurrency.Maximum = 4;
        _concurrency.Dock = DockStyle.Fill;
        _quality.DropDownStyle = ComboBoxStyle.DropDownList;
        _quality.Dock = DockStyle.Fill;
        _quality.DataSource = Enum.GetValues<VideoQuality>();
        ConfigureNumeric(_maxFileSizeGb, 1, 1024, 20);
        ConfigureNumeric(_retryCount, 0, 10, 3);
        ConfigureNumeric(_networkTimeout, 10, 3600, 120);
        ConfigureNumeric(_ffmpegTimeout, 1, 240, 30);
        _autoOpen.Text = "Tự mở thư mục khi hoàn tất";
        _deleteTemp.Text = "Xóa file tạm sau mỗi job";
        _autoStart.Text = "Tự tải sau khi phân tích";
        foreach (var checkBox in new[] { _autoOpen, _deleteTemp, _autoStart })
        {
            checkBox.AutoSize = true;
            checkBox.Margin = new Padding(0, 0, 0, 10);
        }
    }

    private static void ConfigureNumeric(NumericUpDown control, decimal minimum, decimal maximum, decimal value)
    {
        control.Minimum = minimum;
        control.Maximum = maximum;
        control.Value = value;
        control.Dock = DockStyle.Fill;
    }

    private static Control CreateTitle()
    {
        var title = UiTheme.CreateLabel("Cài đặt", 16F, FontStyle.Bold);
        title.ForeColor = UiTheme.PrimaryDark;
        return title;
    }

    private static Control CreatePathField(string labelText, TextBox textBox, EventHandler browseHandler)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        var label = UiTheme.CreateLabel(labelText, 9F, FontStyle.Bold);
        panel.SetColumnSpan(label, 2);
        var browse = UiTheme.CreateSecondaryButton("Chọn...");
        browse.Dock = DockStyle.Fill;
        browse.Width = 82;
        browse.Click += browseHandler;
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(textBox, 0, 1);
        panel.Controls.Add(browse, 1, 1);
        return panel;
    }

    private Control CreateTwoColumnFields()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.Controls.Add(CreateLabeledControl("Download đồng thời", _concurrency), 0, 0);
        layout.Controls.Add(CreateLabeledControl("Chất lượng mặc định", _quality), 1, 0);
        return layout;
    }

    private Control CreateLimitsFields()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.Controls.Add(CreateLabeledControl("Giới hạn file (GB)", _maxFileSizeGb), 0, 0);
        layout.Controls.Add(CreateLabeledControl("Số lần retry", _retryCount), 1, 0);
        layout.Controls.Add(CreateLabeledControl("Network timeout (giây)", _networkTimeout), 0, 1);
        layout.Controls.Add(CreateLabeledControl("FFmpeg timeout (phút)", _ffmpegTimeout), 1, 1);
        return layout;
    }

    private Control CreateCheckBoxPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0)
        };
        panel.Controls.Add(_autoOpen);
        panel.Controls.Add(_deleteTemp);
        panel.Controls.Add(_autoStart);
        return panel;
    }

    private static Control CreateLabeledControl(string text, Control control)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 12, 8) };
        var label = UiTheme.CreateLabel(text, 8.5F, FontStyle.Bold);
        label.Dock = DockStyle.Top;
        control.Dock = DockStyle.Bottom;
        panel.Controls.Add(control);
        panel.Controls.Add(label);
        return panel;
    }

    private async void LoadSettings(object? sender, EventArgs e)
    {
        try
        {
            var settings = await _settingsService.GetAsync().ConfigureAwait(true);
            _downloadFolder.Text = settings.DownloadFolder;
            _ffmpegPath.Text = settings.FfmpegPath ?? string.Empty;
            _concurrency.Value = settings.MaximumConcurrentDownloads;
            _quality.SelectedItem = settings.DefaultQuality;
            _maxFileSizeGb.Value = Math.Clamp(settings.MaxFileSizeBytes / (1024m * 1024 * 1024), 1, 1024);
            _retryCount.Value = settings.MaxRetryCount;
            _networkTimeout.Value = settings.NetworkTimeoutSeconds;
            _ffmpegTimeout.Value = settings.FfmpegTimeoutMinutes;
            _autoOpen.Checked = settings.AutoOpenFolder;
            _deleteTemp.Checked = settings.DeleteTemporaryFiles;
            _autoStart.Checked = settings.StartDownloadAutomatically;
            var validation = await _ffmpegService
                .ValidateAsync(_ffmpegPath.Text, _provisioningCancellation.Token)
                .ConfigureAwait(true);
            _ffmpegStatus.Text = validation.Message;
            _ffmpegStatus.ForeColor = validation.IsValid ? UiTheme.Success : UiTheme.MutedText;
        }
        catch (Exception exception)
        {
            ErrorDialog.Show(this, exception, "Không thể tải cài đặt.");
        }
    }

    private async void SaveClick(object? sender, EventArgs e)
    {
        try
        {
            _saveButton.Enabled = false;
            if (!string.IsNullOrWhiteSpace(_ffmpegPath.Text))
            {
                var validation = await _ffmpegService.ValidateAsync(_ffmpegPath.Text, CancellationToken.None).ConfigureAwait(true);
                if (!validation.IsValid)
                {
                    throw new InvalidOperationException(validation.Message);
                }
            }

            await _settingsService.SaveAsync(new AppSettings
            {
                DownloadFolder = _downloadFolder.Text,
                FfmpegPath = _ffmpegPath.Text,
                MaximumConcurrentDownloads = (int)_concurrency.Value,
                DefaultQuality = (VideoQuality)(_quality.SelectedItem ?? VideoQuality.BestAvailable),
                DefaultFormat = "MP4",
                AutoOpenFolder = _autoOpen.Checked,
                DeleteTemporaryFiles = _deleteTemp.Checked,
                StartDownloadAutomatically = _autoStart.Checked,
                MaxFileSizeBytes = decimal.ToInt64(_maxFileSizeGb.Value * 1024 * 1024 * 1024),
                MaxRetryCount = (int)_retryCount.Value,
                NetworkTimeoutSeconds = (int)_networkTimeout.Value,
                FfmpegTimeoutMinutes = (int)_ffmpegTimeout.Value
            }).ConfigureAwait(true);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to save settings");
            ErrorDialog.Show(this, exception, "Không thể lưu cài đặt.");
        }
        finally
        {
            _saveButton.Enabled = true;
        }
    }

    private async void PrepareFfmpegClick(object? sender, EventArgs e)
    {
        try
        {
            _prepareFfmpegButton.Enabled = false;
            if (!string.IsNullOrWhiteSpace(_ffmpegPath.Text))
            {
                var manual = await _ffmpegService
                    .ValidateAsync(_ffmpegPath.Text, _provisioningCancellation.Token)
                    .ConfigureAwait(true);
                if (manual.IsValid)
                {
                    _ffmpegStatus.Text = manual.Message;
                    _ffmpegStatus.ForeColor = UiTheme.Success;
                    return;
                }
            }

            var progress = new Progress<FFmpegProvisioningProgressDto>(value =>
            {
                _ffmpegStatus.Text = value.Message;
                _ffmpegStatus.ForeColor = value.State switch
                {
                    FFmpegProvisioningState.Ready => UiTheme.Success,
                    FFmpegProvisioningState.Failed => UiTheme.Danger,
                    _ => UiTheme.MutedText
                };
            });
            var result = await _ffmpegProvisioningService
                .EnsureAvailableAsync(progress, _provisioningCancellation.Token)
                .ConfigureAwait(true);
            _ffmpegPath.Text = result.ExecutablePath;
            _ffmpegStatus.Text = $"FFmpeg {result.Version} đã sẵn sàng ({result.Source}).";
            _ffmpegStatus.ForeColor = UiTheme.Success;
        }
        catch (OperationCanceledException) when (_provisioningCancellation.IsCancellationRequested)
        {
            // Settings window is closing.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to prepare FFmpeg");
            _ffmpegStatus.Text = exception.Message;
            _ffmpegStatus.ForeColor = UiTheme.Danger;
        }
        finally
        {
            if (!IsDisposed)
            {
                _prepareFfmpegButton.Enabled = true;
            }
        }
    }

    private void BrowseFolder(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Chọn thư mục tải xuống",
            UseDescriptionForTitle = true,
            SelectedPath = _downloadFolder.Text,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _downloadFolder.Text = dialog.SelectedPath;
        }
    }

    private void BrowseFfmpeg(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Chọn ffmpeg.exe",
            Filter = "FFmpeg executable|ffmpeg.exe|Executable files|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _ffmpegPath.Text = dialog.FileName;
        }
    }
}
