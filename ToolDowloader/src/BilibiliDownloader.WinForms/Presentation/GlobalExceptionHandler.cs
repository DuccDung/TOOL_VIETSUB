using Microsoft.Extensions.Logging;

namespace BilibiliDownloader.WinForms.Presentation;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IDisposable
{
    public void Register()
    {
        System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += ThreadException;
        AppDomain.CurrentDomain.UnhandledException += DomainUnhandledException;
        TaskScheduler.UnobservedTaskException += UnobservedTaskException;
    }

    public void Dispose()
    {
        System.Windows.Forms.Application.ThreadException -= ThreadException;
        AppDomain.CurrentDomain.UnhandledException -= DomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= UnobservedTaskException;
    }

    private void ThreadException(object sender, ThreadExceptionEventArgs e)
    {
        logger.LogError(e.Exception, "Unhandled WinForms UI exception");
        ErrorDialog.Show(Form.ActiveForm, e.Exception, "Ứng dụng gặp lỗi giao diện không mong đợi.");
    }

    private void DomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            logger.LogCritical(exception, "Unhandled application domain exception; terminating: {IsTerminating}", e.IsTerminating);
        }
    }

    private void UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        logger.LogError(e.Exception, "Unobserved background task exception");
        e.SetObserved();
    }
}
