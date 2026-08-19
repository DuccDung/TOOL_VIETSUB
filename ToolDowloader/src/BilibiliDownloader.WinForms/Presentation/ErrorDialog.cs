using BilibiliDownloader.Application.Errors;

namespace BilibiliDownloader.WinForms.Presentation;

internal static class ErrorDialog
{
    public static void Show(IWin32Window? owner, Exception exception, string fallback = "Đã xảy ra lỗi.")
    {
        var (code, message) = exception switch
        {
            AppException appException => (appException.PublicCode, appException.Message),
            OperationCanceledException => ("CANCELLED", "Thao tác đã được hủy."),
            _ => ("UNKNOWN_ERROR", string.IsNullOrWhiteSpace(exception.Message) ? fallback : exception.Message)
        };

        MessageBox.Show(
            owner,
            $"Không thể hoàn tất thao tác.\n\nMã lỗi: {code}\nLý do: {message}",
            "Bilibili Downloader",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
