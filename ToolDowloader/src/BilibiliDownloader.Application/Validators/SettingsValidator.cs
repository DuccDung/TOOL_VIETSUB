using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Domain.Entities;
using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Application.Validators;

public static class SettingsValidator
{
    public static void Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.DownloadFolder))
        {
            throw new AppException(AppErrorCode.UnknownError, "Thư mục tải xuống không được để trống.");
        }

        if (settings.MaximumConcurrentDownloads is < 1 or > 4)
        {
            throw new AppException(AppErrorCode.UnknownError, "Số download đồng thời phải từ 1 đến 4.");
        }

        if (settings.MaxRetryCount is < 0 or > 10)
        {
            throw new AppException(AppErrorCode.UnknownError, "Số lần thử lại phải từ 0 đến 10.");
        }

        if (settings.NetworkTimeoutSeconds is < 10 or > 3600)
        {
            throw new AppException(AppErrorCode.UnknownError, "Network timeout phải từ 10 đến 3600 giây.");
        }

        if (settings.FfmpegTimeoutMinutes is < 1 or > 240)
        {
            throw new AppException(AppErrorCode.UnknownError, "FFmpeg timeout phải từ 1 đến 240 phút.");
        }

        if (settings.MaxFileSizeBytes < 1024 * 1024)
        {
            throw new AppException(AppErrorCode.UnknownError, "Giới hạn file phải ít nhất 1 MB.");
        }
    }
}
