using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using TOOL_VIETSUB_APP.Api;
using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.LocalAi;
using TOOL_VIETSUB_APP.Media;
using TOOL_VIETSUB_APP.Playback;
using TOOL_VIETSUB_APP.Subtitles;

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
    private const string AppHostName = "app.vietsub.local";
    private const string MediaHostName = "media.vietsub.local";

    private readonly WebView2 _webView;
    private readonly AuthSessionManager _authSessionManager = new();
    private readonly DesktopWorkspaceCoordinator _workspaceCoordinator;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly System.Windows.Forms.Timer _sessionTimer;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private bool _authInitialized;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    public MainForm()
    {
        _workspaceCoordinator = new DesktopWorkspaceCoordinator(_authSessionManager);
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
                case "project:subtitle-removal:update":
                    await UpdateOriginalSubtitleRemovalAsync(document.RootElement);
                    break;
                case "project:subtitle-style:update":
                    await UpdateSubtitleStyleAsync(document.RootElement);
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
                    await TranslateAsync();
                    break;
                case "job:voice:synthesize":
                    await SynthesizeVoiceAsync();
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
            var state = await _workspaceCoordinator.UpdateOriginalSubtitleRemovalAsync(
                enabledElement.GetBoolean(),
                mode,
                x,
                y,
                width,
                height,
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

    private async Task TranslateAsync()
    {
        await RunWorkspaceJobAsync(
            "translate",
            token => _workspaceCoordinator.TranslateAsync(token));
    }

    private async Task SynthesizeVoiceAsync()
    {
        await RunWorkspaceJobAsync(
            "synthesize-voice",
            token => _workspaceCoordinator.SynthesizeVoiceAsync(token));
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
            var state = operation switch
            {
                "pause" => await _workspaceCoordinator.PauseJobAsync(jobId, _lifetimeCancellation.Token),
                "resume" => await _workspaceCoordinator.ResumeJobAsync(jobId, _lifetimeCancellation.Token),
                "retry" => await _workspaceCoordinator.RetryJobAsync(jobId, _lifetimeCancellation.Token),
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
            FileName = $"vietsub-{DateTime.Now:yyyyMMdd-HHmm}.mp4",
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
        try
        {
            PostMessage(new
            {
                type = "video:import:started",
                fileName = Path.GetFileName(dialog.FileName),
                mode = mode.ToString().ToUpperInvariant(),
            });
            var state = await _workspaceCoordinator.ImportVideoAsync(
                dialog.FileName,
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
        try
        {
            await _workspaceCoordinator.DisposeAsync();
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
