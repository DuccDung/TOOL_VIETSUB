namespace SubVid.App.Core;

internal enum UiModalScheduleResult
{
    Scheduled,
    Busy,
    Unavailable,
}

internal sealed class UiModalOperationDispatcher
{
    private readonly Action<Func<Task>> _post;
    private readonly Func<bool> _canRun;
    private int _active;

    public UiModalOperationDispatcher(
        Action<Func<Task>> post,
        Func<bool> canRun)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _canRun = canRun ?? throw new ArgumentNullException(nameof(canRun));
    }

    internal bool IsBusy => Volatile.Read(ref _active) != 0;

    public UiModalScheduleResult TrySchedule(
        Func<Task> operation,
        Action<Exception> reportError)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(reportError);

        if (!_canRun())
        {
            return UiModalScheduleResult.Unavailable;
        }

        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            return UiModalScheduleResult.Busy;
        }

        try
        {
            // The callback is posted instead of invoked here so modal WinForms dialogs
            // never start a nested message loop inside a WebView2 event callback.
            _post(() => ExecuteAsync(operation, reportError));
            return UiModalScheduleResult.Scheduled;
        }
        catch
        {
            Interlocked.Exchange(ref _active, 0);
            throw;
        }
    }

    private async Task ExecuteAsync(
        Func<Task> operation,
        Action<Exception> reportError)
    {
        try
        {
            if (_canRun())
            {
                await operation();
            }
        }
        catch (Exception exception)
        {
            try
            {
                reportError(exception);
            }
            catch
            {
                // Error reporting must not escape an async UI callback.
            }
        }
        finally
        {
            Interlocked.Exchange(ref _active, 0);
        }
    }
}
