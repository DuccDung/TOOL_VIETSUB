using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SubVid.App.Api;
using SubVid.App.Core;
using SubVid.App.LocalAi;
using SubVid.App.Media;
using SubVid.App.Playback;
using SubVid.App.Subtitles;

namespace SubVid.App;

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
    private const string AppHostName = "app.subvid.local";
    private const string MediaHostName = "media.subvid.local";

    private readonly WebView2 _webView;
    private readonly AuthSessionManager _authSessionManager = new();
    private readonly DesktopWorkspaceCoordinator _workspaceCoordinator;
    private readonly FfmpegRuntimeProvisioner _ffmpegProvisioner;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly System.Windows.Forms.Timer _sessionTimer;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private bool _authInitialized;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;
    private int _aiStoragePickerQueued;
    private int _aiStorageChangeActive;
    private CancellationTokenSource? _ffmpegInstallCancellation;
    private Task? _ffmpegInstallTask;
    private PendingVideoImport? _pendingVideoImport;

    public MainForm()
    {
        _workspaceCoordinator = new DesktopWorkspaceCoordinator(_authSessionManager);
        _ffmpegProvisioner = new FfmpegRuntimeProvisioner(new AppPaths());
        _workspaceCoordinator.ImportProgressChanged += (_, progress) => PostMessage(new
        {
            type = "video:import:progress",
            progress,
        });
        _workspaceCoordinator.JobChanged += (_, job) => PostFromAnyThread(new
        {
            type = "job:changed",
            job,
        }, includeProjectState: job.Status is LocalJobStatus.Completed or LocalJobStatus.Failed);
        _workspaceCoordinator.ModelDownloadProgressChanged += (_, progress) => PostFromAnyThread(new
        {
            type = "model:download:progress",
            progress,
        });
        _workspaceCoordinator.RuntimeProgressChanged += (_, progress) => PostFromAnyThread(new
        {
            type = "runtime:install:progress",
            progress,
        });
        Text = "SubVid Studio";
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
        _sessionTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _sessionTimer.Tick += OnSessionTimerTick;
        Shown += OnShown;
        FormClosing += OnFormClosing;
        FormClosed += OnFormClosed;
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
                "Không tìm thấy giao diện WebView2. Hãy build lại SubVid.App.",
                indexPath);
        }

        var userDataFolder = Path.Combine(AppPaths.ResolveDefaultRootDirectory(), "WebView2");

        Directory.CreateDirectory(userDataFolder);

        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder);

        await _webView.EnsureCoreWebView2Async(environment);

        var core = _webView.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(
            AppHostName,
            webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);
        core.AddWebResourceRequestedFilter(
            $"https://{MediaHostName}/video",
            CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter(
            $"https://{MediaHostName}/voice",
            CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnMediaResourceRequested;

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
        core.ProcessFailed += OnWebViewProcessFailed;
        core.NavigationStarting += (_, args) =>
        {
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Host, AppHostName, StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.NavigationCompleted += (_, _) => SendWindowState();

        var uiVersion = File.GetLastWriteTimeUtc(indexPath).Ticks;
        _webView.Source = new Uri($"https://{AppHostName}/index.html?v={uiVersion}");
    }

    private void OnWebViewProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs eventArgs)
    {
        try
        {
            var logDirectory = Path.Combine(AppPaths.ResolveDefaultRootDirectory(), "Logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "webview-process.log"),
                $"{DateTimeOffset.Now:O}\t{eventArgs.ProcessFailedKind}\tWebView2 process failed.{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine(exception);
#endif
        }

        if (eventArgs.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited
            || IsDisposed
            || Disposing)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed && !Disposing && _webView.CoreWebView2 is not null)
                {
                    _webView.Reload();
                }
            }));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine(exception);
#endif
        }
    }

    private void OnMediaResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs eventArgs)
    {
        if (!Uri.TryCreate(eventArgs.Request.Uri, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, MediaHostName, StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath is not ("/video" or "/voice"))
        {
            return;
        }

        try
        {
            var sourcePath = uri.AbsolutePath == "/voice"
                ? _workspaceCoordinator.GetCurrentVoiceTimelinePath()
                : _workspaceCoordinator.GetCurrentSourcePath();
            var file = new FileInfo(sourcePath);
            string? rangeHeader = null;
            try
            {
                rangeHeader = eventArgs.Request.Headers.GetHeader("Range");
            }
            catch (Exception exception) when (exception is ArgumentException or COMException)
            {
                // A missing Range header means the browser requests the full file.
            }

            if (!LocalMediaRange.TryParse(rangeHeader, file.Length, out var range))
            {
                eventArgs.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    Stream.Null,
                    416,
                    "Range Not Satisfiable",
                    $"Content-Range: bytes */{file.Length}\r\nAccept-Ranges: bytes");
                return;
            }

            var isPartial = !string.IsNullOrWhiteSpace(rangeHeader);
            Stream content = Stream.Null;
            if (!string.Equals(eventArgs.Request.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                var fileStream = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);
                content = new BoundedReadStream(fileStream, range.Start, range.Length);
            }

            var responseHeaders = new List<string>
            {
                $"Content-Type: {GetMediaContentType(file.Extension)}",
                $"Content-Length: {range.Length}",
                "Accept-Ranges: bytes",
                $"Access-Control-Allow-Origin: https://{AppHostName}",
                "Cache-Control: no-store"
            };
            if (isPartial)
            {
                responseHeaders.Add($"Content-Range: bytes {range.Start}-{range.End}/{file.Length}");
            }

            var headers = string.Join("\r\n", responseHeaders);
            eventArgs.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                content,
                isPartial ? 206 : 200,
                isPartial ? "Partial Content" : "OK",
                headers);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException)
        {
            eventArgs.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                Stream.Null,
                404,
                "Not Found",
                "Cache-Control: no-store");
        }
    }

    private static string GetMediaContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };

    private async void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            if (!Uri.TryCreate(eventArgs.Source, UriKind.Absolute, out var sourceUri)
                || !string.Equals(sourceUri.Host, AppHostName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

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
                    await OpenVideoAsync(document.RootElement);
                    break;
                case "video:import:cancel":
                    _workspaceCoordinator.CancelImport();
                    break;
                case "ffmpeg:status":
                    PostFfmpegStatus();
                    break;
                case "ffmpeg:install":
                    if (_ffmpegInstallTask is null || _ffmpegInstallTask.IsCompleted)
                    {
                        _ffmpegInstallTask = InstallFfmpegAsync(document.RootElement);
                    }
                    await _ffmpegInstallTask;
                    break;
                case "ffmpeg:install:cancel":
                    CancelFfmpegInstall();
                    break;
                case "ffmpeg:folder:select":
                    await SelectFfmpegFolderAsync();
                    break;
                case "ffmpeg:folder:open":
                    OpenFfmpegFolder();
                    break;
                case "video:export":
                    await ExportVideoAsync();
                    break;
                case "project:list":
                    await SendProjectListAsync();
                    break;
                case "project:create":
                    await CreateProjectAsync(document.RootElement);
                    break;
                case "project:open":
                    await OpenProjectAsync(document.RootElement);
                    break;
                case "project:rename":
                    await RenameProjectAsync(document.RootElement);
                    break;
                case "project:settings:update":
                    await UpdateProjectSettingsAsync(document.RootElement);
                    break;
                case "project:translation-settings:update":
                    await UpdateTranslationSettingsAsync(document.RootElement);
                    break;
                case "project:audio-settings:update":
                    await UpdateAudioSettingsAsync(document.RootElement);
                    break;
                case "project:voice-settings:update":
                    await UpdateVoiceSettingsAsync(document.RootElement);
                    break;
                case "project:voice-cloud-settings:update":
                    await UpdateFptVoiceCredentialAsync(document.RootElement);
                    break;
                case "voice:model:install":
                    await InstallVoiceAsync(document.RootElement);
                    break;
                case "voice:cloud:preview":
                    await PreviewFptVoiceAsync(document.RootElement);
                    break;
                case "ai-storage:select":
                    QueueAiStorageSelection();
                    break;
                case "ai-storage:change":
                    await ChangeAiStorageAsync(document.RootElement);
                    break;
                case "ai-storage:discard-pending":
                    await DiscardPendingAiStorageMigrationAsync();
                    break;
                case "project:subtitle-removal:update":
                    await UpdateOriginalSubtitleRemovalAsync(document.RootElement);
                    break;
                case "project:video-transform:update":
                    await UpdateVideoTransformAsync(document.RootElement);
                    break;
                case "project:subtitle-style:update":
                    await UpdateSubtitleStyleAsync(document.RootElement);
                    break;
                case "project:vietnamese-subtitles:update":
                    await UpdateVietnameseSubtitlesEnabledAsync(document.RootElement);
                    break;
                case "job:audio:prepare":
                    await PrepareAudioAsync();
                    break;
                case "job:transcribe":
                    await TranscribeAsync();
                    break;
                case "job:ocr":
                    await RunOcrAsync();
                    break;
                case "job:translate":
                    await TranslateAsync(document.RootElement);
                    break;
                case "job:voice:synthesize":
                    await SynthesizeVoiceAsync(document.RootElement);
                    break;
                case "job:pause":
                    await ChangeJobStateAsync(document.RootElement, "pause");
                    break;
                case "job:resume":
                    await ChangeJobStateAsync(document.RootElement, "resume");
                    break;
                case "job:retry":
                    await ChangeJobStateAsync(document.RootElement, "retry");
                    break;
                case "job:cancel":
                    await ChangeJobStateAsync(document.RootElement, "cancel");
                    break;
                case "subtitle:import:srt":
                    await ImportSrtAsync();
                    break;
                case "subtitle:update":
                    await UpdateSubtitleAsync(document.RootElement);
                    break;
                case "subtitle:voice:update":
                    await UpdateSubtitleVoiceAsync(document.RootElement);
                    break;
                case "timeline:split":
                    await EditTimelineAsync(document.RootElement, "split");
                    break;
                case "timeline:align":
                    await EditTimelineAsync(document.RootElement, "align");
                    break;
                case "timeline:duplicate":
                    await EditTimelineAsync(document.RootElement, "duplicate");
                    break;
                case "timeline:delete":
                    await EditTimelineAsync(document.RootElement, "delete");
                    break;
                case "subtitle:export:srt":
                    await ExportSrtAsync();
                    break;
                case "app:ready":
                    SendWindowState();
                    await InitializeAuthAsync();
                    PostFfmpegStatus();
                    break;
                case "auth:login":
                    await LoginAsync(document.RootElement);
                    break;
                case "auth:register:start":
                    await StartRegistrationAsync(document.RootElement);
                    break;
                case "auth:register:verify":
                    await VerifyRegistrationAsync(document.RootElement);
                    break;
                case "auth:register:resend":
                    await ResendRegistrationAsync(document.RootElement);
                    break;
                case "auth:logout":
                    PostAuthBusy();
                    await _workspaceCoordinator.CloseCurrentAsync(_lifetimeCancellation.Token);
                    PostAuthState(await _authSessionManager.LogoutAsync(_lifetimeCancellation.Token));
                    break;
                case "auth:refresh":
                    PostAuthBusy();
                    PostAuthState(await _authSessionManager.RefreshAccountAsync(_lifetimeCancellation.Token));
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed UI messages. The native host must stay responsive.
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (Exception exception)
        {
            _ = exception;
            PostAuthState(new DesktopAuthState(
                "error",
                null,
                null,
                null,
                "APP_BRIDGE_ERROR",
                "Ứng dụng không thể xử lý yêu cầu từ giao diện."));
#if DEBUG
            System.Diagnostics.Debug.WriteLine(exception);
#endif
        }
    }

    private async Task InitializeAuthAsync()
    {
        if (_authInitialized)
        {
            PostAuthState(_authSessionManager.CurrentState);
            return;
        }

        _authInitialized = true;
        PostAuthBusy();
        var state = await _authSessionManager.InitializeAsync(_lifetimeCancellation.Token);
        PostAuthState(state);
        if (state.IsAuthenticated)
        {
            await SendProjectListAsync();
        }
        _sessionTimer.Start();
    }

    private async Task LoginAsync(JsonElement message)
    {
        if (!message.TryGetProperty("email", out var emailElement)
            || emailElement.ValueKind != JsonValueKind.String
            || !message.TryGetProperty("password", out var passwordElement)
            || passwordElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var email = emailElement.GetString() ?? string.Empty;
        var password = passwordElement.GetString() ?? string.Empty;
        PostAuthBusy();
        var state = await _authSessionManager.LoginAsync(
            email,
            password,
            _lifetimeCancellation.Token);
        PostAuthState(state);
        if (state.IsAuthenticated)
        {
            await SendProjectListAsync();
        }
    }

    private async Task StartRegistrationAsync(JsonElement message)
    {
        if (!TryGetRequiredString(message, "displayName", out var displayName)
            || !TryGetRequiredString(message, "email", out var email)
            || !TryGetRequiredString(message, "password", out var password, trim: false))
        {
            PostRegistrationError("REGISTRATION_REQUEST_INVALID", "Thông tin đăng ký không hợp lệ.");
            return;
        }

        PostRegistrationBusy("start");
        var result = await _authSessionManager.StartRegistrationAsync(
            displayName,
            email,
            password,
            _lifetimeCancellation.Token);
        PostRegistrationResult(result);
    }

    private async Task VerifyRegistrationAsync(JsonElement message)
    {
        if (!TryGetRequiredString(message, "challengeId", out var challengeText)
            || !Guid.TryParse(challengeText, out var challengeId)
            || !TryGetRequiredString(message, "otp", out var otp)
            || otp.Length != 6
            || otp.Any(character => !char.IsAsciiDigit(character)))
        {
            PostRegistrationError("REGISTRATION_REQUEST_INVALID", "Mã OTP phải gồm đúng 6 chữ số.");
            return;
        }

        PostRegistrationBusy("verify");
        var result = await _authSessionManager.VerifyRegistrationAsync(
            challengeId,
            otp,
            _lifetimeCancellation.Token);
        if (result.Succeeded && result.AuthState is not null)
        {
            PostAuthState(result.AuthState);
            await SendProjectListAsync();
            return;
        }

        PostRegistrationResult(result);
    }

    private async Task ResendRegistrationAsync(JsonElement message)
    {
        if (!TryGetRequiredString(message, "challengeId", out var challengeText)
            || !Guid.TryParse(challengeText, out var challengeId))
        {
            PostRegistrationError("REGISTRATION_REQUEST_INVALID", "Yêu cầu gửi lại OTP không hợp lệ.");
            return;
        }

        PostRegistrationBusy("resend");
        var result = await _authSessionManager.ResendRegistrationAsync(
            challengeId,
            _lifetimeCancellation.Token);
        PostRegistrationResult(result);
    }

    private static bool TryGetRequiredString(
        JsonElement message,
        string propertyName,
        out string value,
        bool trim = true)
    {
        value = string.Empty;
        if (!message.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        if (trim)
        {
            value = value.Trim();
        }

        return value.Length > 0;
    }

    private async void OnSessionTimerTick(object? sender, EventArgs eventArgs)
    {
        try
        {
            var previousState = _authSessionManager.CurrentState;
            var nextState = await _authSessionManager.RefreshIfNeededAsync(_lifetimeCancellation.Token);
            if (!ReferenceEquals(previousState, nextState))
            {
                PostAuthState(nextState);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }

    private void PostAuthBusy()
    {
        PostMessage(new
        {
            type = "auth:state",
            state = new DesktopAuthState("loading", null, null, null),
        });
    }

    private void PostAuthState(DesktopAuthState state)
    {
        PostMessage(new { type = "auth:state", state });
    }

    private void PostRegistrationBusy(string operation)
    {
        PostMessage(new { type = "auth:register:busy", operation });
    }

    private void PostRegistrationResult(DesktopRegistrationResult result)
    {
        if (result.Succeeded && result.Challenge is not null)
        {
            PostMessage(new
            {
                type = "auth:register:challenge",
                challenge = result.Challenge,
            });
            return;
        }

        PostRegistrationError(
            result.ErrorCode ?? "REGISTRATION_FAILED",
            result.ErrorMessage ?? "Chưa thể xử lý đăng ký. Vui lòng thử lại.");
    }

    private void PostRegistrationError(string code, string message)
    {
        PostMessage(new { type = "auth:register:error", code, message });
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

    private async Task CreateProjectAsync(JsonElement message)
    {
        if (!TryGetRequiredString(message, "name", out var name))
        {
            PostWorkspaceError("PROJECT_NAME_INVALID", "Hãy nhập tên dự án.");
            return;
        }

        try
        {
            PostMessage(new { type = "project:busy", operation = "create" });
            var state = await _workspaceCoordinator.CreateAsync(name, _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
            await SendProjectListAsync();
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task OpenProjectAsync(JsonElement message)
    {
        if (!TryGetRequiredString(message, "projectId", out var projectText)
            || !Guid.TryParse(projectText, out var projectId))
        {
            PostWorkspaceError("PROJECT_ID_INVALID", "Mã dự án không hợp lệ.");
            return;
        }

        try
        {
            PostMessage(new { type = "project:busy", operation = "open" });
            var state = await _workspaceCoordinator.OpenAsync(projectId, _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
            await SendProjectListAsync();
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task RenameProjectAsync(JsonElement message)
    {
        if (!TryGetRequiredString(message, "name", out var name))
        {
            PostWorkspaceError("PROJECT_NAME_INVALID", "Hãy nhập tên dự án.");
            return;
        }

        try
        {
            PostMessage(new { type = "project:busy", operation = "rename" });
            var state = await _workspaceCoordinator.RenameAsync(name, _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
            await SendProjectListAsync();
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task UpdateProjectSettingsAsync(JsonElement message)
    {
        if (!TryGetRequiredString(message, "sourceLanguageCode", out var sourceLanguageCode)
            || !TryGetRequiredString(message, "ocrLanguageCode", out var ocrLanguageCode))
        {
            PostWorkspaceError("PROJECT_SETTINGS_INVALID", "Thiết lập ngôn ngữ không hợp lệ.");
            return;
        }

        try
        {
            var state = await _workspaceCoordinator.UpdateLanguageSettingsAsync(
                sourceLanguageCode,
                ocrLanguageCode,
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task UpdateTranslationSettingsAsync(JsonElement message)
    {
        if (!TryGetRequiredString(message, "provider", out var provider)
            || !TryGetRequiredString(message, "modelId", out var modelId)
            || !TryGetRequiredString(message, "qualityMode", out var qualityMode)
            || !message.TryGetProperty("reviewEnabled", out var reviewElement)
            || reviewElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !message.TryGetProperty("fallbackToLocal", out var fallbackElement)
            || fallbackElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !message.TryGetProperty("projectContext", out var contextElement)
            || contextElement.ValueKind != JsonValueKind.String
            || !message.TryGetProperty("characterInstructions", out var characterElement)
            || characterElement.ValueKind != JsonValueKind.String
            || !message.TryGetProperty("styleInstructions", out var styleElement)
            || styleElement.ValueKind != JsonValueKind.String
            || !message.TryGetProperty("glossaryText", out var glossaryElement)
            || glossaryElement.ValueKind != JsonValueKind.String)
        {
            PostWorkspaceError("TRANSLATION_SETTINGS_INVALID", "Thiết lập dịch không hợp lệ.");
            return;
        }

        var apiKey = message.TryGetProperty("apiKey", out var apiKeyElement)
            && apiKeyElement.ValueKind == JsonValueKind.String
            ? apiKeyElement.GetString()
            : null;
        var clearApiKey = message.TryGetProperty("clearApiKey", out var clearElement)
            && clearElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && clearElement.GetBoolean();
        try
        {
            var state = await _workspaceCoordinator.UpdateTranslationSettingsAsync(
                provider,
                modelId,
                qualityMode,
                reviewElement.GetBoolean(),
                fallbackElement.GetBoolean(),
                contextElement.GetString() ?? string.Empty,
                characterElement.GetString() ?? string.Empty,
                styleElement.GetString() ?? string.Empty,
                glossaryElement.GetString() ?? string.Empty,
                apiKey,
                clearApiKey,
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
            PostMessage(new { type = "translation:settings:saved" });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task UpdateAudioSettingsAsync(JsonElement message)
    {
        if (!message.TryGetProperty("originalAudioEnabled", out var originalEnabledElement)
            || originalEnabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !TryGetFiniteDouble(message, "originalAudioVolumePercent", out var originalVolume)
            || !message.TryGetProperty("vietnameseVoiceEnabled", out var voiceEnabledElement)
            || voiceEnabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !TryGetFiniteDouble(message, "vietnameseVoiceVolumePercent", out var voiceVolume))
        {
            PostWorkspaceError("PROJECT_AUDIO_SETTINGS_INVALID", "Thiết lập âm thanh không hợp lệ.");
            return;
        }

        try
        {
            var state = await _workspaceCoordinator.UpdateAudioSettingsAsync(
                originalEnabledElement.GetBoolean(),
                originalVolume,
                voiceEnabledElement.GetBoolean(),
                voiceVolume,
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task UpdateVoiceSettingsAsync(JsonElement message)
    {
        if (!TryGetRequiredString(message, "defaultVoiceId", out var defaultVoiceId)
            || !message.TryGetProperty("speakerVoiceIds", out var mappingsElement)
            || mappingsElement.ValueKind != JsonValueKind.Object)
        {
            PostWorkspaceError("PROJECT_VOICE_SETTINGS_INVALID", "Thiết lập giọng đọc không hợp lệ.");
            return;
        }

        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in mappingsElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                PostWorkspaceError("PROJECT_VOICE_SETTINGS_INVALID", "Giọng đọc theo nhân vật không hợp lệ.");
                return;
            }

            mappings[property.Name] = property.Value.GetString()!;
        }

        var speed = message.TryGetProperty("speed", out var speedElement)
            && speedElement.TryGetInt32(out var parsedSpeed)
                ? parsedSpeed
                : 0;

        try
        {
            var state = await _workspaceCoordinator.UpdateVoiceSettingsAsync(
                defaultVoiceId,
                mappings,
                speed,
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
            PostMessage(new { type = "voice:settings:saved" });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task UpdateFptVoiceCredentialAsync(JsonElement message)
    {
        var apiKey = message.TryGetProperty("apiKey", out var apiKeyElement)
            && apiKeyElement.ValueKind == JsonValueKind.String
                ? apiKeyElement.GetString()
                : null;
        var clearApiKey = message.TryGetProperty("clearApiKey", out var clearElement)
            && clearElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && clearElement.GetBoolean();
        try
        {
            var state = await _workspaceCoordinator.UpdateFptVoiceCredentialAsync(
                apiKey,
                clearApiKey,
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
            PostMessage(new { type = "voice:cloud:settings:saved" });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task PreviewFptVoiceAsync(JsonElement message)
    {
        if (!TryGetRequiredString(message, "voiceId", out var voiceId))
        {
            PostWorkspaceError("VOICE_ID_INVALID", "Giọng FPT.AI cần nghe thử không hợp lệ.");
            return;
        }

        var speed = message.TryGetProperty("speed", out var speedElement)
            && speedElement.TryGetInt32(out var parsedSpeed)
                ? parsedSpeed
                : 0;
        var apiKey = message.TryGetProperty("apiKey", out var apiKeyElement)
            && apiKeyElement.ValueKind == JsonValueKind.String
                ? apiKeyElement.GetString()
                : null;
        try
        {
            var bytes = await _workspaceCoordinator.PreviewFptVoiceAsync(
                voiceId,
                speed,
                apiKey,
                _lifetimeCancellation.Token);
            PostMessage(new
            {
                type = "project:state",
                project = _workspaceCoordinator.GetCurrentState(),
            });
            PostMessage(new
            {
                type = "voice:cloud:previewed",
                voiceId,
                audioDataUrl = "data:audio/wav;base64," + Convert.ToBase64String(bytes),
            });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task InstallVoiceAsync(JsonElement message)
    {
        if (!TryGetRequiredString(message, "voiceId", out var voiceId))
        {
            PostWorkspaceError("VOICE_ID_INVALID", "Giọng đọc cần cài không hợp lệ.");
            return;
        }

        try
        {
            var state = await _workspaceCoordinator.InstallVoiceAsync(
                voiceId,
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
            PostMessage(new { type = "voice:model:installed", voiceId });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private void QueueAiStorageSelection()
    {
        if (_workspaceCoordinator.CurrentProject is null)
        {
            PostWorkspaceError("PROJECT_REQUIRED", "Hãy mở dự án trước khi đổi thư mục AI local.");
            return;
        }

        if (Interlocked.CompareExchange(ref _aiStoragePickerQueued, 1, 0) != 0
            || Volatile.Read(ref _aiStorageChangeActive) != 0)
        {
            PostWorkspaceError("AI_STORAGE_BUSY", "Một thao tác chọn hoặc chuyển thư mục AI đang được xử lý.");
            return;
        }

        try
        {
            // FolderBrowserDialog must not start a nested native message loop while
            // WebView2 is still dispatching WebMessageReceived.
            BeginInvoke(new Action(ShowAiStorageSelectionDialog));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            Interlocked.Exchange(ref _aiStoragePickerQueued, 0);
            HandleWorkspaceException(exception);
        }
    }

    private void ShowAiStorageSelectionDialog()
    {
        try
        {
            if (IsDisposed || Disposing || _workspaceCoordinator.CurrentProject is null)
            {
                return;
            }

            var current = _workspaceCoordinator.AiStorageStatus;
            using var dialog = new FolderBrowserDialog
            {
                Description = "Chọn thư mục riêng để lưu runtime, model và cache AI local",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = Directory.Exists(current.RootPath)
                    ? current.RootPath
                    : current.RecommendedPath,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                PostMessage(new { type = "ai-storage:selection-cancelled" });
                return;
            }

            PostMessage(new
            {
                type = "ai-storage:selected",
                destinationPath = Path.GetFullPath(dialog.SelectedPath),
                currentPath = current.RootPath,
            });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _aiStoragePickerQueued, 0);
        }
    }

    private async Task ChangeAiStorageAsync(JsonElement message)
    {
        if (!TryGetRequiredString(message, "destinationPath", out var destinationPath)
            || !message.TryGetProperty("migrateExisting", out var migrateElement)
            || migrateElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            PostWorkspaceError("AI_STORAGE_INVALID", "Yêu cầu chuyển thư mục AI không hợp lệ.");
            return;
        }

        if (Interlocked.CompareExchange(ref _aiStorageChangeActive, 1, 0) != 0)
        {
            PostWorkspaceError("AI_STORAGE_BUSY", "Một thao tác chuyển thư mục AI đang chạy.");
            return;
        }

        var current = _workspaceCoordinator.AiStorageStatus;
        try
        {
            PostMessage(new
            {
                type = "ai-storage:busy",
                destinationPath,
                migrateExisting = migrateElement.GetBoolean(),
            });
            var state = await _workspaceCoordinator.ChangeAiStorageAsync(
                destinationPath,
                migrateElement.GetBoolean(),
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
            PostMessage(new
            {
                type = "ai-storage:saved",
                previousPath = current.RootPath,
                destinationPath = state.AiStorage.RootPath,
            });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _aiStorageChangeActive, 0);
        }
    }

    private async Task DiscardPendingAiStorageMigrationAsync()
    {
        if (Interlocked.CompareExchange(ref _aiStorageChangeActive, 1, 0) != 0)
        {
            PostWorkspaceError("AI_STORAGE_BUSY", "Một thao tác chuyển thư mục AI đang chạy.");
            return;
        }

        try
        {
            PostMessage(new { type = "ai-storage:busy", operation = "discard" });
            var state = await _workspaceCoordinator.DiscardPendingAiStorageMigrationAsync(
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
            PostMessage(new { type = "ai-storage:discarded" });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _aiStorageChangeActive, 0);
        }
    }

    private async Task UpdateOriginalSubtitleRemovalAsync(JsonElement message)
    {
        if (!message.TryGetProperty("enabled", out var enabledElement)
            || enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !TryGetRequiredString(message, "mode", out var mode)
            || !TryGetFiniteDouble(message, "x", out var x)
            || !TryGetFiniteDouble(message, "y", out var y)
            || !TryGetFiniteDouble(message, "width", out var width)
            || !TryGetFiniteDouble(message, "height", out var height))
        {
            PostWorkspaceError("PROJECT_SETTINGS_INVALID", "Thiết lập vùng xóa phụ đề gốc không hợp lệ.");
            return;
        }

        try
        {
            var regions = new List<SubtitleRemovalRegionSettings>();
            if (message.TryGetProperty("regions", out var regionsElement))
            {
                if (regionsElement.ValueKind != JsonValueKind.Array
                    || regionsElement.GetArrayLength() is < 1 or > DesktopWorkspaceCoordinator.MaxSubtitleRemovalRegions)
                {
                    PostWorkspaceError("PROJECT_SETTINGS_INVALID", "Danh sách vùng che không hợp lệ.");
                    return;
                }

                foreach (var regionElement in regionsElement.EnumerateArray())
                {
                    if (regionElement.ValueKind != JsonValueKind.Object
                        || !TryGetFiniteDouble(regionElement, "x", out var regionX)
                        || !TryGetFiniteDouble(regionElement, "y", out var regionY)
                        || !TryGetFiniteDouble(regionElement, "width", out var regionWidth)
                        || !TryGetFiniteDouble(regionElement, "height", out var regionHeight))
                    {
                        PostWorkspaceError("PROJECT_SETTINGS_INVALID", "Tọa độ vùng che không hợp lệ.");
                        return;
                    }

                    var regionId = regionElement.TryGetProperty("id", out var idElement)
                        && idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString() ?? string.Empty
                        : string.Empty;
                    regions.Add(new SubtitleRemovalRegionSettings
                    {
                        Id = regionId,
                        X = regionX,
                        Y = regionY,
                        Width = regionWidth,
                        Height = regionHeight,
                    });
                }
            }
            else
            {
                regions.Add(new SubtitleRemovalRegionSettings
                {
                    Id = "legacy",
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height,
                });
            }

            var state = await _workspaceCoordinator.UpdateOriginalSubtitleRemovalAsync(
                enabledElement.GetBoolean(),
                mode,
                x,
                y,
                width,
                height,
                regions,
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task UpdateVideoTransformAsync(JsonElement message)
    {
        if (!message.TryGetProperty("flipHorizontal", out var horizontalElement)
            || horizontalElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !message.TryGetProperty("flipVertical", out var verticalElement)
            || verticalElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            PostWorkspaceError("PROJECT_VIDEO_TRANSFORM_INVALID", "Thiết lập lật video không hợp lệ.");
            return;
        }

        try
        {
            var state = await _workspaceCoordinator.UpdateVideoTransformAsync(
                horizontalElement.GetBoolean(),
                verticalElement.GetBoolean(),
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task UpdateSubtitleStyleAsync(JsonElement message)
    {
        SubtitleStyleSettings? style;
        try
        {
            style = JsonSerializer.Deserialize<SubtitleStyleSettings>(message.GetRawText(), _jsonOptions);
        }
        catch (JsonException)
        {
            style = null;
        }

        if (!SubtitleStyleRules.TryValidate(style, out var error))
        {
            PostWorkspaceError("PROJECT_SUBTITLE_STYLE_INVALID", error);
            return;
        }

        try
        {
            var state = await _workspaceCoordinator.UpdateSubtitleStyleAsync(
                style!,
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task UpdateVietnameseSubtitlesEnabledAsync(JsonElement message)
    {
        if (!message.TryGetProperty("enabled", out var enabledElement)
            || enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            PostWorkspaceError("PROJECT_SUBTITLE_VISIBILITY_INVALID", "Thiết lập hiển thị phụ đề Việt không hợp lệ.");
            return;
        }

        try
        {
            var state = await _workspaceCoordinator.UpdateVietnameseSubtitlesEnabledAsync(
                enabledElement.GetBoolean(),
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private static bool TryGetFiniteDouble(JsonElement message, string propertyName, out double value)
    {
        value = 0;
        return message.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value)
            && double.IsFinite(value);
    }

    private async Task SendProjectListAsync()
    {
        if (!_authSessionManager.CurrentState.IsAuthenticated)
        {
            return;
        }

        try
        {
            var projects = await _workspaceCoordinator.ListAsync(_lifetimeCancellation.Token);
            PostMessage(new { type = "project:list", projects });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task PrepareAudioAsync()
    {
        try
        {
            PostMessage(new { type = "job:busy", operation = "prepare-audio" });
            var state = await _workspaceCoordinator.PrepareAudioAsync(_lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task TranscribeAsync()
    {
        try
        {
            PostMessage(new { type = "job:busy", operation = "transcribe" });
            var state = await _workspaceCoordinator.TranscribeAsync(_lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task RunOcrAsync()
    {
        try
        {
            PostMessage(new { type = "job:busy", operation = "ocr" });
            var state = await _workspaceCoordinator.RunOcrAsync(_lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task TranslateAsync(JsonElement message)
    {
        var translationRunMode = message.TryGetProperty("translationMode", out var modeElement)
            && modeElement.ValueKind == JsonValueKind.String
                ? modeElement.GetString()
                : null;
        await RunWorkspaceJobAsync(
            "translate",
            token => _workspaceCoordinator.TranslateAsync(token, translationRunMode));
    }

    private async Task SynthesizeVoiceAsync(JsonElement message)
    {
        var voiceId = message.TryGetProperty("voiceId", out var voiceIdElement)
            && voiceIdElement.ValueKind == JsonValueKind.String
                ? voiceIdElement.GetString()
                : null;
        var speed = message.TryGetProperty("speed", out var speedElement)
            && speedElement.TryGetInt32(out var parsedSpeed)
                ? parsedSpeed
                : (int?)null;
        var apiKey = message.TryGetProperty("apiKey", out var apiKeyElement)
            && apiKeyElement.ValueKind == JsonValueKind.String
                ? apiKeyElement.GetString()
                : null;
        await RunWorkspaceJobAsync(
            "synthesize-voice",
            token => _workspaceCoordinator.SynthesizeVoiceAsync(voiceId, speed, apiKey, token));
    }

    private async Task RunWorkspaceJobAsync(
        string operation,
        Func<CancellationToken, Task<DesktopProjectState>> start)
    {
        try
        {
            PostMessage(new { type = "job:busy", operation });
            var state = await start(_lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task ChangeJobStateAsync(JsonElement message, string operation)
    {
        if (!TryGetRequiredString(message, "jobId", out var jobText)
            || !Guid.TryParse(jobText, out var jobId))
        {
            PostWorkspaceError("JOB_ID_INVALID", "Mã công việc không hợp lệ.");
            return;
        }

        try
        {
            PostMessage(new { type = "job:busy", operation, jobId });
            var translationRunMode = message.TryGetProperty("translationMode", out var modeElement)
                && modeElement.ValueKind == JsonValueKind.String
                    ? modeElement.GetString()
                    : null;
            var state = operation switch
            {
                "pause" => await _workspaceCoordinator.PauseJobAsync(jobId, _lifetimeCancellation.Token),
                "resume" => await _workspaceCoordinator.ResumeJobAsync(jobId, _lifetimeCancellation.Token),
                "retry" => await _workspaceCoordinator.RetryJobAsync(
                    jobId,
                    _lifetimeCancellation.Token,
                    translationRunMode),
                "cancel" => await _workspaceCoordinator.CancelJobAsync(jobId, _lifetimeCancellation.Token),
                _ => throw new InvalidOperationException("Thao tác job không được hỗ trợ."),
            };
            PostMessage(new { type = "project:state", project = state });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task ImportSrtAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Nhập phụ đề SRT UTF-8",
            Filter = "Phụ đề SubRip|*.srt",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            PostMessage(new { type = "subtitle:busy", operation = "import" });
            var state = await _workspaceCoordinator.ImportSrtAsync(
                dialog.FileName,
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
            PostMessage(new { type = "subtitle:saved", operation = "import" });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task UpdateSubtitleAsync(JsonElement message)
    {
        if (!TryGetRequiredString(message, "cueId", out var cueText)
            || !Guid.TryParse(cueText, out var cueId)
            || !TryGetRequiredString(message, "original", out var original, trim: false)
            || !message.TryGetProperty("translated", out var translatedElement)
            || translatedElement.ValueKind != JsonValueKind.String)
        {
            PostWorkspaceError("SUBTITLE_REQUEST_INVALID", "Nội dung phụ đề không hợp lệ.");
            return;
        }

        try
        {
            PostMessage(new { type = "subtitle:busy", operation = "update" });
            var state = await _workspaceCoordinator.UpdateSubtitleAsync(
                cueId,
                original,
                translatedElement.GetString() ?? string.Empty,
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
            PostMessage(new { type = "subtitle:saved", operation = "update" });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task UpdateSubtitleVoiceAsync(JsonElement message)
    {
        if (!TryGetRequiredString(message, "cueId", out var cueText)
            || !Guid.TryParse(cueText, out var cueId)
            || !TryGetRequiredString(message, "speaker", out var speaker))
        {
            PostWorkspaceError("SUBTITLE_VOICE_REQUEST_INVALID", "Thiết lập giọng đọc của phân đoạn không hợp lệ.");
            return;
        }

        string? voiceId = null;
        if (message.TryGetProperty("voiceId", out var voiceElement)
            && voiceElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (voiceElement.ValueKind != JsonValueKind.String)
            {
                PostWorkspaceError("SUBTITLE_VOICE_REQUEST_INVALID", "Mã giọng đọc không hợp lệ.");
                return;
            }

            voiceId = voiceElement.GetString();
        }

        try
        {
            var state = await _workspaceCoordinator.UpdateSubtitleVoiceAsync(
                cueId,
                speaker,
                voiceId,
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
            PostMessage(new { type = "subtitle:saved", operation = "voice" });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task EditTimelineAsync(JsonElement message, string operation)
    {
        if (!TryGetRequiredString(message, "cueId", out var cueText)
            || !Guid.TryParse(cueText, out var cueId))
        {
            PostWorkspaceError("SUBTITLE_REQUEST_INVALID", "Mã phân đoạn phụ đề không hợp lệ.");
            return;
        }

        var requiresPosition = operation is "split" or "align";
        var positionSeconds = 0d;
        if (requiresPosition
            && (!message.TryGetProperty("positionSeconds", out var positionElement)
                || positionElement.ValueKind != JsonValueKind.Number
                || !positionElement.TryGetDouble(out positionSeconds)
                || !double.IsFinite(positionSeconds)))
        {
            PostWorkspaceError("SUBTITLE_REQUEST_INVALID", "Vị trí playhead không hợp lệ.");
            return;
        }

        try
        {
            PostMessage(new { type = "subtitle:busy", operation });
            var state = operation switch
            {
                "split" => await _workspaceCoordinator.SplitSubtitleCueAsync(
                    cueId, positionSeconds, _lifetimeCancellation.Token),
                "align" => await _workspaceCoordinator.AlignSubtitleCueAsync(
                    cueId, positionSeconds, _lifetimeCancellation.Token),
                "duplicate" => await _workspaceCoordinator.DuplicateSubtitleCueAsync(
                    cueId, _lifetimeCancellation.Token),
                "delete" => await _workspaceCoordinator.DeleteSubtitleCueAsync(
                    cueId, _lifetimeCancellation.Token),
                _ => throw new InvalidOperationException("Thao tác timeline không được hỗ trợ."),
            };
            PostMessage(new { type = "project:state", project = state });
            PostMessage(new { type = "subtitle:saved", operation = "timeline" });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task ExportSrtAsync()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Xuất phụ đề SRT",
            Filter = "Phụ đề SubRip|*.srt",
            DefaultExt = "srt",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = "phu-de-tieng-viet.srt",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            PostMessage(new { type = "subtitle:busy", operation = "export" });
            await _workspaceCoordinator.ExportSrtAsync(dialog.FileName, _lifetimeCancellation.Token);
            PostMessage(new { type = "subtitle:saved", operation = "export" });
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task ExportVideoAsync()
    {
        if (_workspaceCoordinator.CurrentProject?.SourceVideo is null)
        {
            PostWorkspaceError("MEDIA_SOURCE_MISSING", "Hãy nhập video trước khi xuất.");
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Xuất video tiếng Việt",
            Filter = "Video MP4|*.mp4",
            DefaultExt = "mp4",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"subvid-{DateTime.Now:yyyyMMdd-HHmm}.mp4",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await RunWorkspaceJobAsync(
            "export-video",
            token => _workspaceCoordinator.ExportVideoAsync(dialog.FileName, token));
    }

    private async Task OpenVideoAsync(JsonElement message)
    {
        if (_workspaceCoordinator.CurrentProject is null)
        {
            PostWorkspaceError("PROJECT_REQUIRED", "Hãy tạo hoặc mở một dự án trước khi nhập video.");
            return;
        }

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

        var mode = message.TryGetProperty("mode", out var modeElement)
            && string.Equals(modeElement.GetString(), "link", StringComparison.OrdinalIgnoreCase)
            ? MediaImportMode.Link
            : MediaImportMode.Copy;
        if (!_ffmpegProvisioner.GetStatus().Ready)
        {
            _pendingVideoImport = new PendingVideoImport(dialog.FileName, mode);
            PostMessage(new
            {
                type = "ffmpeg:install:required",
                status = _ffmpegProvisioner.GetStatus(),
                fileName = Path.GetFileName(dialog.FileName),
            });
            return;
        }

        await ImportVideoFileAsync(dialog.FileName, mode);
    }

    private async Task ImportVideoFileAsync(string fileName, MediaImportMode mode)
    {
        try
        {
            PostMessage(new
            {
                type = "video:import:started",
                fileName = Path.GetFileName(fileName),
                mode = mode.ToString().ToUpperInvariant(),
            });
            var state = await _workspaceCoordinator.ImportVideoAsync(
                fileName,
                mode,
                _lifetimeCancellation.Token);
            PostMessage(new { type = "project:state", project = state });
            PostMessage(new { type = "video:import:completed", video = state.Video });
            await SendProjectListAsync();
        }
        catch (OperationCanceledException)
        {
            PostWorkspaceError("MEDIA_IMPORT_CANCELLED", "Đã hủy nhập video.");
        }
        catch (Exception exception)
        {
            HandleWorkspaceException(exception);
        }
    }

    private async Task InstallFfmpegAsync(JsonElement message)
    {
        if (_ffmpegInstallCancellation is not null)
        {
            PostMessage(new
            {
                type = "ffmpeg:install:failed",
                code = "FFMPEG_INSTALL_BUSY",
                message = "Một lượt cài FFmpeg đang chạy.",
            });
            return;
        }

        var force = message.TryGetProperty("force", out var forceElement)
            && forceElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && forceElement.GetBoolean();
        _ffmpegInstallCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var progress = new Progress<FfmpegInstallProgress>(value => PostMessage(new
        {
            type = "ffmpeg:install:progress",
            progress = value,
        }));
        try
        {
            var status = await _ffmpegProvisioner.EnsureReadyAsync(
                progress,
                force,
                _ffmpegInstallCancellation.Token);
            PostMessage(new { type = "ffmpeg:install:completed", status });
            var pending = _pendingVideoImport;
            _pendingVideoImport = null;
            if (pending is not null)
            {
                await ImportVideoFileAsync(pending.FileName, pending.Mode);
            }
        }
        catch (OperationCanceledException) when (_ffmpegInstallCancellation.IsCancellationRequested)
        {
            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                PostMessage(new { type = "ffmpeg:install:cancelled", status = _ffmpegProvisioner.GetStatus() });
            }
        }
        catch (FfmpegRuntimeException exception)
        {
            PostMessage(new
            {
                type = "ffmpeg:install:failed",
                code = exception.Code,
                message = exception.Message,
                status = _ffmpegProvisioner.GetStatus(),
            });
        }
        finally
        {
            _ffmpegInstallCancellation.Dispose();
            _ffmpegInstallCancellation = null;
        }
    }

    private void CancelFfmpegInstall()
    {
        _pendingVideoImport = null;
        if (_ffmpegInstallCancellation is not null)
        {
            _ffmpegInstallCancellation.Cancel();
            return;
        }

        PostMessage(new { type = "ffmpeg:install:cancelled", status = _ffmpegProvisioner.GetStatus() });
    }

    private async Task SelectFfmpegFolderAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Chọn thư mục chứa ffmpeg.exe và ffprobe.exe",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            InitialDirectory = _ffmpegProvisioner.ManagedDirectory,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var status = _ffmpegProvisioner.UseExternalDirectory(dialog.SelectedPath);
            PostMessage(new { type = "ffmpeg:status", status });
            var pending = _pendingVideoImport;
            _pendingVideoImport = null;
            if (pending is not null)
            {
                await ImportVideoFileAsync(pending.FileName, pending.Mode);
            }
        }
        catch (FfmpegRuntimeException exception)
        {
            PostMessage(new
            {
                type = "ffmpeg:install:failed",
                code = exception.Code,
                message = exception.Message,
                status = _ffmpegProvisioner.GetStatus(),
            });
        }
    }

    private void OpenFfmpegFolder()
    {
        var status = _ffmpegProvisioner.GetStatus();
        var directory = status.FfmpegPath is null
            ? status.InstallDirectory
            : Path.GetDirectoryName(status.FfmpegPath) ?? status.InstallDirectory;
        Directory.CreateDirectory(directory);
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add(directory);
        System.Diagnostics.Process.Start(startInfo);
    }

    private void PostFfmpegStatus() => PostMessage(new
    {
        type = "ffmpeg:status",
        status = _ffmpegProvisioner.GetStatus(),
    });

    private void HandleWorkspaceException(Exception exception)
    {
        if (exception is ApiClientException apiException)
        {
            if (apiException.IsAuthenticationFailure)
            {
                PostAuthState(_authSessionManager.CurrentState);
            }

            PostWorkspaceError(apiException.Code, apiException.Message);
            return;
        }

        if (exception is MediaInspectionException mediaException)
        {
            PostWorkspaceError(mediaException.Code, mediaException.Message);
            return;
        }

        if (exception is FfmpegRuntimeException ffmpegException)
        {
            PostWorkspaceError(ffmpegException.Code, ffmpegException.Message);
            return;
        }

        if (exception is SrtException srtException)
        {
            PostWorkspaceError(srtException.Code, srtException.Message);
            return;
        }

        if (exception is LocalModelException modelException)
        {
            PostWorkspaceError(modelException.Code, modelException.Message);
            return;
        }

        var code = exception switch
        {
            UnauthorizedAccessException => "PROJECT_ACCESS_DENIED",
            FileNotFoundException => "PROJECT_NOT_FOUND",
            InvalidDataException => "PROJECT_DATA_INVALID",
            InvalidOperationException => "WORKSPACE_INVALID_OPERATION",
            _ => "WORKSPACE_ERROR",
        };
        PostWorkspaceError(code, exception.Message);
#if DEBUG
        System.Diagnostics.Debug.WriteLine(exception);
#endif
    }

    private void PostWorkspaceError(string code, string message)
    {
        PostMessage(new { type = "workspace:error", code, message });
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

        _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, _jsonOptions));
    }

    private void PostFromAnyThread(object payload, bool includeProjectState = false)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        void Post()
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            PostMessage(payload);
            if (includeProjectState && _workspaceCoordinator.CurrentProject is not null)
            {
                PostMessage(new
                {
                    type = "project:state",
                    project = _workspaceCoordinator.GetCurrentState(),
                });
            }
        }

        if (InvokeRequired)
        {
            BeginInvoke(Post);
        }
        else
        {
            Post();
        }
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        _sessionTimer.Stop();
        _lifetimeCancellation.Cancel();
        _ffmpegInstallCancellation?.Cancel();
        try
        {
            await _workspaceCoordinator.DisposeAsync();
            if (_ffmpegInstallTask is not null)
            {
                await _ffmpegInstallTask;
            }
        }
        catch (Exception exception)
        {
#if !DEBUG
            _ = exception;
#endif
#if DEBUG
            System.Diagnostics.Debug.WriteLine(exception);
#endif
        }

        _shutdownCompleted = true;
        BeginInvoke(Close);
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs eventArgs)
    {
        _sessionTimer.Stop();
        _sessionTimer.Dispose();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _authSessionManager.Dispose();
        _ffmpegProvisioner.Dispose();
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

    private sealed record PendingVideoImport(string FileName, MediaImportMode Mode);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        int wParam,
        int lParam);
}
