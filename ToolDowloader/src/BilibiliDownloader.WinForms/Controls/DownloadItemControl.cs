using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Domain.Models;
using BilibiliDownloader.WinForms.Presentation;

namespace BilibiliDownloader.WinForms.Controls;

public sealed class DownloadItemControl : UserControl
{
    private readonly Label _title;
    private readonly Label _format;
    private readonly DownloadProgressControl _progress;
    private readonly Button _cancelButton;
    private readonly Button _openButton;
    private DownloadJobSnapshot? _snapshot;

    public DownloadItemControl()
    {
        Height = 132;
        Width = 960;
        Margin = new Padding(0, 0, 0, 12);
        Padding = new Padding(16, 14, 16, 12);
        BackColor = UiTheme.Surface;
        Paint += UiTheme.PaintRoundedPanel;

        _title = UiTheme.CreateLabel("Video", 10.5F, FontStyle.Bold);
        _title.AutoEllipsis = true;
        _title.Dock = DockStyle.Top;
        _format = UiTheme.CreateLabel("MP4", 8.5F);
        _format.ForeColor = UiTheme.MutedText;
        _format.Dock = DockStyle.Top;
        _progress = new DownloadProgressControl { Dock = DockStyle.Fill };
        _cancelButton = UiTheme.CreateSecondaryButton("Hủy");
        _cancelButton.Width = 90;
        _cancelButton.Click += (_, _) =>
        {
            if (_snapshot is not null)
            {
                CancelRequested?.Invoke(this, _snapshot.Id);
            }
        };
        _openButton = UiTheme.CreateSecondaryButton("Mở thư mục");
        _openButton.Width = 110;
        _openButton.Enabled = false;
        _openButton.Click += (_, _) =>
        {
            if (_snapshot?.OutputPath is not null)
            {
                OpenFolderRequested?.Invoke(this, _snapshot.OutputPath);
            }
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 220,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 25, 0, 0)
        };
        buttons.Controls.Add(_openButton);
        buttons.Controls.Add(_cancelButton);
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 18, 0) };
        content.Controls.Add(_progress);
        content.Controls.Add(_format);
        content.Controls.Add(_title);
        Controls.Add(content);
        Controls.Add(buttons);
    }

    public event EventHandler<Guid>? CancelRequested;
    public event EventHandler<string>? OpenFolderRequested;

    public void UpdateJob(DownloadJobSnapshot snapshot)
    {
        _snapshot = snapshot;
        _title.Text = snapshot.Title;
        _format.Text = $"{snapshot.Quality}  •  {snapshot.Format}";
        _progress.UpdateProgress(snapshot);
        _cancelButton.Enabled = snapshot.Status is not (DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled);
        _openButton.Enabled = snapshot.Status == DownloadStatus.Completed && !string.IsNullOrWhiteSpace(snapshot.OutputPath);
        if (snapshot.Status == DownloadStatus.Failed && !string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            _format.Text += $"  •  {snapshot.ErrorMessage}";
            _format.ForeColor = UiTheme.Danger;
        }
    }
}
