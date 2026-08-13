namespace TOOL_VIETSUB_APP.Api;

public sealed class AuthSessionManager : IDisposable
{
    private readonly DesktopApiClient _apiClient = new();
    private readonly ProtectedSessionStore _sessionStore = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private StoredAuthSession? _storedSession;
    private DateTime _accessTokenExpiresAtUtc;
    private string _deviceId = Guid.NewGuid().ToString("N");

    public DesktopAuthState CurrentState { get; private set; } =
        new("loading", null, null, null);

    public async Task<DesktopAuthState> InitializeAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _storedSession = _sessionStore.Load();
            if (_storedSession is null)
            {
                return SetSignedOut();
            }

            _deviceId = _storedSession.DeviceId;
            if (_storedSession.RefreshTokenExpiresAtUtc <= DateTime.UtcNow)
            {
                ClearSession();
                return SetSignedOut("AUTH_REFRESH_EXPIRED", "Phiên đăng nhập đã hết hạn.");
            }

            return await RefreshCoreAsync(_storedSession, cancellationToken);
        }
        catch (ApiClientException exception) when (exception.IsAuthenticationFailure)
        {
            ClearSession();
            return SetSignedOut(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return SetError("SERVER_UNAVAILABLE", "Không thể kết nối tới Server. Hãy kiểm tra kết nối rồi thử lại.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<DesktopAuthState> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            ClearSession();
            var tokens = await _apiClient.LoginAsync(
                new LoginApiRequest(
                    email.Trim(),
                    password,
                    _deviceId,
                    Environment.MachineName,
                    GetAppVersion()),
                cancellationToken);
            return await AcceptTokensAsync(tokens, cancellationToken);
        }
        catch (ApiClientException exception)
        {
            return SetSignedOut(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return SetSignedOut(
                "SERVER_UNAVAILABLE",
                "Không thể kết nối tới Server. Hãy kiểm tra Server đang chạy.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<DesktopRegistrationResult> StartRegistrationAsync(
        string displayName,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var challenge = await _apiClient.StartRegistrationAsync(
                new RegistrationStartApiRequest(
                    displayName.Trim(),
                    email.Trim(),
                    password,
                    _deviceId,
                    Environment.MachineName,
                    GetAppVersion()),
                cancellationToken);
            return new DesktopRegistrationResult(true, Challenge: challenge);
        }
        catch (ApiClientException exception)
        {
            return RegistrationFailure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return RegistrationFailure(
                "SERVER_UNAVAILABLE",
                "Không thể kết nối tới Server. Hãy kiểm tra Server đang chạy.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<DesktopRegistrationResult> VerifyRegistrationAsync(
        Guid challengeId,
        string otp,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            ClearSession();
            var tokens = await _apiClient.VerifyRegistrationAsync(
                new RegistrationVerifyApiRequest(
                    challengeId,
                    otp,
                    _deviceId,
                    Environment.MachineName,
                    GetAppVersion()),
                cancellationToken);
            var state = await AcceptTokensAsync(tokens, cancellationToken);
            return new DesktopRegistrationResult(true, AuthState: state);
        }
        catch (ApiClientException exception)
        {
            return RegistrationFailure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return RegistrationFailure(
                "SERVER_UNAVAILABLE",
                "Không thể kết nối tới Server. Hãy kiểm tra Server đang chạy.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<DesktopRegistrationResult> ResendRegistrationAsync(
        Guid challengeId,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var challenge = await _apiClient.ResendRegistrationAsync(
                new RegistrationResendApiRequest(challengeId, _deviceId),
                cancellationToken);
            return new DesktopRegistrationResult(true, Challenge: challenge);
        }
        catch (ApiClientException exception)
        {
            return RegistrationFailure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return RegistrationFailure(
                "SERVER_UNAVAILABLE",
                "Không thể kết nối tới Server. Hãy kiểm tra Server đang chạy.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<DesktopAuthState> RefreshAccountAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_storedSession is null)
            {
                return SetSignedOut();
            }

            if (_accessTokenExpiresAtUtc <= DateTime.UtcNow.AddMinutes(2))
            {
                return await RefreshCoreAsync(_storedSession, cancellationToken);
            }

            return await LoadAccountStateAsync(CurrentState.Account, cancellationToken);
        }
        catch (ApiClientException exception) when (exception.IsAuthenticationFailure)
        {
            ClearSession();
            return SetSignedOut(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return SetError("SERVER_UNAVAILABLE", "Không thể làm mới thông tin tài khoản.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<DesktopAuthState> RefreshIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_storedSession is null || _accessTokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(2))
        {
            return CurrentState;
        }

        return await RefreshAccountAsync(cancellationToken);
    }

    public async Task<DesktopAuthState> LogoutAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_storedSession is not null)
            {
                if (_accessTokenExpiresAtUtc <= DateTime.UtcNow.AddSeconds(30))
                {
                    await RefreshCoreAsync(_storedSession, cancellationToken);
                }

                await _apiClient.LogoutAsync(cancellationToken);
            }
        }
        catch (ApiClientException)
        {
            // Local logout must still complete if the Server already revoked the session.
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The refresh token is removed locally; it expires or can be revoked later.
        }
        finally
        {
            ClearSession();
            _operationLock.Release();
        }

        return SetSignedOut();
    }

    public async Task<T> ExecuteAuthenticatedAsync<T>(
        Func<DesktopApiClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_storedSession is null)
            {
                throw new ApiClientException(
                    "AUTH_REQUIRED",
                    "Vui lòng đăng nhập để tiếp tục.",
                    401);
            }

            if (_accessTokenExpiresAtUtc <= DateTime.UtcNow.AddMinutes(2))
            {
                await RefreshCoreAsync(_storedSession, cancellationToken);
            }

            return await operation(_apiClient, cancellationToken);
        }
        catch (ApiClientException exception) when (exception.IsAuthenticationFailure)
        {
            ClearSession();
            SetSignedOut(exception.Code, exception.Message);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<DesktopAuthState> RefreshCoreAsync(
        StoredAuthSession storedSession,
        CancellationToken cancellationToken)
    {
        var tokens = await _apiClient.RefreshAsync(
            new RefreshApiRequest(
                storedSession.RefreshToken,
                storedSession.DeviceId,
                Environment.MachineName,
                GetAppVersion()),
            cancellationToken);
        return await AcceptTokensAsync(tokens, cancellationToken);
    }

    private async Task<DesktopAuthState> AcceptTokensAsync(
        TokenPairResponse tokens,
        CancellationToken cancellationToken)
    {
        _apiClient.SetAccessToken(tokens.AccessToken);
        _accessTokenExpiresAtUtc = tokens.AccessTokenExpiresAtUtc;
        _storedSession = new StoredAuthSession(
            tokens.RefreshToken,
            tokens.RefreshTokenExpiresAtUtc,
            _deviceId);
        _sessionStore.Save(_storedSession);
        return await LoadAccountStateAsync(tokens.Account, cancellationToken);
    }

    private async Task<DesktopAuthState> LoadAccountStateAsync(
        AccountResponse? knownAccount,
        CancellationToken cancellationToken)
    {
        var accountTask = knownAccount is null
            ? _apiClient.GetAccountAsync(cancellationToken)
            : Task.FromResult(knownAccount);
        var entitlementsTask = _apiClient.GetEntitlementsAsync(cancellationToken);
        var historyTask = _apiClient.GetUsageHistoryAsync(1, 20, cancellationToken);
        await Task.WhenAll(accountTask, entitlementsTask, historyTask);
        CurrentState = new DesktopAuthState(
            "authenticated",
            await accountTask,
            await entitlementsTask,
            await historyTask);
        return CurrentState;
    }

    private DesktopAuthState SetSignedOut(string? errorCode = null, string? errorMessage = null)
    {
        CurrentState = new DesktopAuthState(
            "unauthenticated",
            null,
            null,
            null,
            errorCode,
            errorMessage);
        return CurrentState;
    }

    private DesktopAuthState SetError(string errorCode, string errorMessage)
    {
        CurrentState = new DesktopAuthState(
            "error",
            null,
            null,
            null,
            errorCode,
            errorMessage);
        return CurrentState;
    }

    private void ClearSession()
    {
        _storedSession = null;
        _accessTokenExpiresAtUtc = default;
        _apiClient.SetAccessToken(null);
        _sessionStore.Delete();
    }

    private static DesktopRegistrationResult RegistrationFailure(string code, string message) =>
        new(false, ErrorCode: code, ErrorMessage: message);

    private static string GetAppVersion() =>
        typeof(AuthSessionManager).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public void Dispose()
    {
        _operationLock.Dispose();
        _apiClient.Dispose();
    }
}
