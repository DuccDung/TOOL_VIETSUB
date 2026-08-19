using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.WinForms.Presentation;

namespace BilibiliDownloader.WinForms.Controls;

public sealed class VideoInfoControl : UserControl
{
    private readonly PictureBox _thumbnail;
    private readonly Label _title;
    private readonly Label _author;
    private readonly Label _duration;
    private readonly Label _videoId;

    public VideoInfoControl()
    {
        BackColor = UiTheme.Surface;
        Dock = DockStyle.Fill;
        Padding = new Padding(18);

        _thumbnail = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(234, 238, 245),
            SizeMode = PictureBoxSizeMode.Zoom
        };
        _title = UiTheme.CreateLabel("Chưa phân tích video", 13F, FontStyle.Bold);
        _title.AutoEllipsis = true;
        _title.Dock = DockStyle.Top;
        _title.MaximumSize = new Size(600, 52);
        _author = UiTheme.CreateLabel("Tác giả: —", 9.5F);
        _duration = UiTheme.CreateLabel("Thời lượng: —", 9.5F);
        _videoId = UiTheme.CreateLabel("Video ID: —", 9.5F);
        foreach (var label in new[] { _author, _duration, _videoId })
        {
            label.ForeColor = UiTheme.MutedText;
            label.Dock = DockStyle.Top;
            label.Padding = new Padding(0, 8, 0, 0);
        }

        var details = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 4, 0, 0) };
        details.Controls.Add(_videoId);
        details.Controls.Add(_duration);
        details.Controls.Add(_author);
        details.Controls.Add(_title);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(_thumbnail, 0, 0);
        layout.Controls.Add(details, 1, 0);
        Controls.Add(layout);
    }

    public void ShowVideo(BilibiliVideoDto video, Image? thumbnail)
    {
        _title.Text = video.Title;
        _author.Text = $"Tác giả: {video.Author}";
        _duration.Text = $"Thời lượng: {Formatters.Duration(video.Duration)}";
        _videoId.Text = $"Video ID: {video.Id}";
        var previous = _thumbnail.Image;
        _thumbnail.Image = thumbnail;
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnail.Image?.Dispose();
        }

        base.Dispose(disposing);
    }
}
