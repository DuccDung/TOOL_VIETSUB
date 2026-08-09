using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TOOL_VIETSUB_APP;

public sealed class MainForm : Form
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int ResizeGrip = 7;

    private readonly WebView2 _webView;

    public MainForm()
    {
        Text = "TOOL VIETSUB Studio";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1180, 720);
        Size = new Size(1480, 860);
        BackColor = Color.FromArgb(6, 10, 18);
        FormBorderStyle = FormBorderStyle.None;
        KeyPreview = true;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(6, 10, 18),
            DefaultBackgroundColor = Color.FromArgb(6, 10, 18),
        };

        Controls.Add(_webView);
        Shown += OnShown;
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        Shown -= OnShown;

        try
        {
            await InitializeWebViewAsync();
        }
        catch (Exception exception)
        {
            ShowStartupError(exception);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var indexPath = Path.Combine(webRoot, "index.html");

        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException(
                "Không tìm thấy giao diện WebView2. Hãy build lại TOOL_VIETSUB_APP.",
                indexPath);
        }

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TOOL_VIETSUB",
            "WebView2");

        Directory.CreateDirectory(userDataFolder);

        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder);

        await _webView.EnsureCoreWebView2Async(environment);

        var core = _webView.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(
            "app.vietsub.local",
            webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
#endif
        core.WebMessageReceived += OnWebMessageReceived;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.NavigationCompleted += (_, _) => SendWindowState();

        _webView.Source = new Uri("https://app.vietsub.local/index.html");
    }

    private void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            using var document = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            if (!document.RootElement.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "window:minimize":
                    WindowState = FormWindowState.Minimized;
                    break;
                case "window:maximize":
                    ToggleMaximize();
                    break;
                case "window:close":
                    Close();
                    break;
                case "window:drag":
                    BeginWindowDrag();
                    break;
                case "video:open":
                    OpenVideo();
                    break;
                case "app:ready":
                    SendWindowState();
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed UI messages. The native host must stay responsive.
        }
    }

    private void ToggleMaximize()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
        }
        else
        {
            MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
            WindowState = FormWindowState.Maximized;
        }

        SendWindowState();
    }

    private void BeginWindowDrag()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            ToggleMaximize();
        }

        ReleaseCapture();
        SendMessage(Handle, WmNcLeftButtonDown, HtCaption, 0);
    }

    private void OpenVideo()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Chọn video cần dịch và lồng tiếng",
            Filter = "Video được hỗ trợ|*.mp4;*.mkv;*.mov;*.webm|Tất cả tệp|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var file = new FileInfo(dialog.FileName);
        PostMessage(new
        {
            type = "video:selected",
            fileName = file.Name,
            extension = file.Extension.TrimStart('.').ToUpperInvariant(),
            sizeBytes = file.Length,
        });
    }

    private void SendWindowState()
    {
        PostMessage(new
        {
            type = "window:state",
            maximized = WindowState == FormWindowState.Maximized,
        });
    }

    private void PostMessage(object payload)
    {
        if (_webView.CoreWebView2 is null)
        {
            return;
        }

        _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
    }

    private void ShowStartupError(Exception exception)
    {
        Controls.Clear();
        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(6, 10, 18),
            ForeColor = Color.FromArgb(226, 232, 240),
            Font = new Font("Segoe UI", 11F),
            Padding = new Padding(32),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = $"Không thể khởi tạo giao diện.\n\n{exception.Message}",
        });
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref message);

            var screenPoint = new Point(
                unchecked((short)(long)message.LParam),
                unchecked((short)((long)message.LParam >> 16)));
            var clientPoint = PointToClient(screenPoint);

            var left = clientPoint.X <= ResizeGrip;
            var right = clientPoint.X >= ClientSize.Width - ResizeGrip;
            var top = clientPoint.Y <= ResizeGrip;
            var bottom = clientPoint.Y >= ClientSize.Height - ResizeGrip;

            message.Result = (left, right, top, bottom) switch
            {
                (true, _, true, _) => (IntPtr)HtTopLeft,
                (_, true, true, _) => (IntPtr)HtTopRight,
                (true, _, _, true) => (IntPtr)HtBottomLeft,
                (_, true, _, true) => (IntPtr)HtBottomRight,
                (true, _, _, _) => (IntPtr)HtLeft,
                (_, true, _, _) => (IntPtr)HtRight,
                (_, _, true, _) => (IntPtr)HtTop,
                (_, _, _, true) => (IntPtr)HtBottom,
                _ => message.Result,
            };

            return;
        }

        base.WndProc(ref message);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        int wParam,
        int lParam);
}
