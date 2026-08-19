using BilibiliDownloader.Domain.Models;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.WinForms.Presentation;

namespace BilibiliDownloader.WinForms.Controls;

public sealed class DownloadProgressControl : UserControl
{
    private readonly ProgressBar _progress;
    private readonly Label _status;
    private readonly Label _metrics;

    public DownloadProgressControl()
    {
        Height = 58;
        Dock = DockStyle.Top;
        BackColor = UiTheme.Surface;
        _status = UiTheme.CreateLabel("Đang chờ", 9F, FontStyle.Bold);
        _status.Dock = DockStyle.Top;
        _metrics = UiTheme.CreateLabel(string.Empty, 8.5F);
        _metrics.ForeColor = UiTheme.MutedText;
        _metrics.Dock = DockStyle.Bottom;
        _progress = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 9,
            Minimum = 0,
            Maximum = 1000,
            Style = ProgressBarStyle.Continuous,
            Margin = new Padding(0, 6, 0, 4)
        };

        var progressHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 18) };
        progressHost.Controls.Add(_progress);
        Controls.Add(progressHost);
        Controls.Add(_metrics);
        Controls.Add(_status);
    }

    public void UpdateProgress(DownloadJobSnapshot snapshot)
    {
        _progress.Value = Math.Clamp((int)Math.Round(snapshot.Percentage * 10), 0, 1000);
        _status.Text = $"{StageText(snapshot)}  {snapshot.Percentage:0.#}%";
        var bytes = snapshot.TotalBytes is > 0
            ? $"{Formatters.Bytes(snapshot.DownloadedBytes)} / {Formatters.Bytes(snapshot.TotalBytes.Value)}"
            : Formatters.Bytes(snapshot.DownloadedBytes);
        var speed = snapshot.SpeedBytesPerSecond > 0
            ? $"{Formatters.Bytes((long)snapshot.SpeedBytesPerSecond)}/s"
            : "—";
        var eta = snapshot.RemainingTime is not null ? $"ETA {Formatters.Duration(snapshot.RemainingTime.Value)}" : "ETA —";
        _metrics.Text = $"{bytes}    {speed}    {eta}";
    }

    private static string StageText(DownloadJobSnapshot snapshot) => snapshot.Stage switch
    {
        DownloadStage.Waiting => "Đang chờ",
        DownloadStage.Resolving => "Đang chuẩn bị công cụ / stream",
        DownloadStage.DownloadingVideo => "Đang tải video",
        DownloadStage.DownloadingAudio => "Đang tải audio",
        DownloadStage.Merging => "Đang ghép bằng FFmpeg",
        DownloadStage.Finalizing => "Đang hoàn tất",
        DownloadStage.Completed => "Hoàn tất",
        DownloadStage.Cancelled => "Đã hủy",
        DownloadStage.Failed => "Thất bại",
        _ => snapshot.Stage.ToString()
    };
}
