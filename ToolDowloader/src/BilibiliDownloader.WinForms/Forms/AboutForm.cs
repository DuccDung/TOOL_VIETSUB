using BilibiliDownloader.WinForms.Presentation;

namespace BilibiliDownloader.WinForms.Forms;

public sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = "Giới thiệu";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(500, 330);
        BackColor = UiTheme.Surface;
        Padding = new Padding(32);
        var title = UiTheme.CreateLabel("Bilibili Downloader", 19F, FontStyle.Bold);
        title.ForeColor = UiTheme.PrimaryDark;
        title.Dock = DockStyle.Top;
        title.Height = 48;
        var version = UiTheme.CreateLabel("Version 1.0.0 • .NET 9 • Windows x64", 9.5F);
        version.ForeColor = UiTheme.MutedText;
        version.Dock = DockStyle.Top;
        version.Height = 38;
        var description = UiTheme.CreateLabel(
            "Ứng dụng desktop local để phân tích và tải nội dung Bilibili mà người dùng có quyền truy cập.\n\n" +
            "Ứng dụng không vượt DRM, CAPTCHA, paywall, đăng nhập, giới hạn khu vực hoặc bất kỳ cơ chế kiểm soát truy cập nào.",
            10F);
        description.Dock = DockStyle.Fill;
        description.MaximumSize = new Size(430, 150);
        var close = UiTheme.CreatePrimaryButton("Đóng");
        close.Dock = DockStyle.Bottom;
        close.Width = 100;
        close.Click += (_, _) => Close();
        Controls.Add(description);
        Controls.Add(version);
        Controls.Add(title);
        Controls.Add(close);
    }
}
