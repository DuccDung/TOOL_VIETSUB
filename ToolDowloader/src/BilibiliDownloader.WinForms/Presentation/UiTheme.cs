using System.Drawing.Drawing2D;

namespace BilibiliDownloader.WinForms.Presentation;

internal static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(245, 247, 251);
    public static readonly Color Surface = Color.White;
    public static readonly Color Primary = Color.FromArgb(0, 161, 214);
    public static readonly Color PrimaryDark = Color.FromArgb(0, 126, 176);
    public static readonly Color Text = Color.FromArgb(32, 41, 56);
    public static readonly Color MutedText = Color.FromArgb(101, 113, 133);
    public static readonly Color Border = Color.FromArgb(224, 229, 238);
    public static readonly Color Success = Color.FromArgb(29, 155, 98);
    public static readonly Color Danger = Color.FromArgb(220, 68, 78);

    public static Button CreatePrimaryButton(string text)
    {
        var button = CreateButton(text);
        button.BackColor = Primary;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    public static Button CreateSecondaryButton(string text)
    {
        var button = CreateButton(text);
        button.BackColor = Surface;
        button.ForeColor = Text;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    public static Label CreateLabel(string text, float size = 9F, FontStyle style = FontStyle.Regular) => new()
    {
        AutoSize = true,
        Text = text,
        Font = new Font("Segoe UI", size, style),
        ForeColor = Text,
        Margin = new Padding(0)
    };

    public static void PaintRoundedPanel(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel panel)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Border);
        var rectangle = new Rectangle(0, 0, panel.Width - 1, panel.Height - 1);
        e.Graphics.DrawRectangle(pen, rectangle);
    }

    private static Button CreateButton(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Height = 36,
        Width = 130,
        FlatStyle = FlatStyle.Flat,
        Cursor = Cursors.Hand,
        Font = new Font("Segoe UI Semibold", 9F),
        Margin = new Padding(6, 0, 0, 0)
    };
}
