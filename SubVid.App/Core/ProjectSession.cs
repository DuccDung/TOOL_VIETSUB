namespace SubVid.App.Core;

public sealed class ProjectSession : IAsyncDisposable
{
    private readonly ProjectWorkspaceService _workspace;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private readonly TimeSpan _autosaveDelay;
    private CancellationTokenSource? _autosaveCancellation;
    private FileStream? _workspaceLock;
    private bool _disposed;
    private bool _started;

    public ProjectSession(
        ProjectWorkspaceService workspace,
        ProjectManifest manifest,
        TimeSpan? autosaveDelay = null)
    {
        _workspace = workspace;
        Manifest = manifest;
        _autosaveDelay = autosaveDelay ?? TimeSpan.FromMilliseconds(750);
    }

    public ProjectManifest Manifest { get; }

    public event EventHandler<ProjectManifest>? Saved;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _workspaceLock ??= _workspace.AcquireExclusiveLock(Manifest.ProjectId);
        try
        {
            Manifest.LastCleanShutdown = false;
            Manifest.LastOpenedAtUtc = DateTime.UtcNow;
            await _workspace.SaveAsync(Manifest, cancellationToken);
            _started = true;
        }
        catch
        {
            _workspaceLock.Dispose();
            _workspaceLock = null;
            throw;
        }
    }

    public async Task UpdateAsync(
        Action<ProjectManifest> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ThrowIfDisposed();
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            update(Manifest);
            ScheduleAutosave();
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_started)
        {
            return;
        }
        CancelPendingAutosave();
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            await _workspace.SaveAsync(Manifest, cancellationToken);
            Saved?.Invoke(this, Manifest);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_started)
        {
            return;
        }

        CancelPendingAutosave();
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            Manifest.LastCleanShutdown = true;
            await _workspace.SaveAsync(Manifest, cancellationToken);
            Saved?.Invoke(this, Manifest);
            _started = false;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private void ScheduleAutosave()
    {
        CancelPendingAutosave();
        var cancellation = new CancellationTokenSource();
        _autosaveCancellation = cancellation;
        _ = AutosaveAfterDelayAsync(cancellation);
    }

    private async Task AutosaveAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_autosaveDelay, cancellation.Token);
            await _mutationLock.WaitAsync(cancellation.Token);
            try
            {
                await _workspace.SaveAsync(Manifest, cancellation.Token);
                Saved?.Invoke(this, Manifest);
            }
            finally
            {
                _mutationLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer edit replaced this autosave request.
        }
        finally
        {
            if (ReferenceEquals(_autosaveCancellation, cancellation))
            {
                _autosaveCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelPendingAutosave()
    {
        var cancellation = Interlocked.Exchange(ref _autosaveCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        CancelPendingAutosave();
        await _mutationLock.WaitAsync();
        try
        {
            if (_started)
            {
                Manifest.LastCleanShutdown = true;
                await _workspace.SaveAsync(Manifest);
            }
            _disposed = true;
        }
        finally
        {
            _workspaceLock?.Dispose();
            _workspaceLock = null;
            _mutationLock.Release();
            _mutationLock.Dispose();
        }
    }
}
