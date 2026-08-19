using System.Buffers;
using System.Diagnostics;
using System.Net;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BilibiliDownloader.Infrastructure.FFmpeg;

public sealed record FFmpegPackageDownloadProgress(
    long DownloadedBytes,
    long? TotalBytes,
    double SpeedBytesPerSecond);

public interface IFFmpegPackageDownloader
{
    Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<FFmpegPackageDownloadProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class FFmpegPackageDownloader(
    HttpClient httpClient,
    IOptions<FFmpegOptions> options,
    ILogger<FFmpegPackageDownloader> logger) : IFFmpegPackageDownloader
{
    private const int BufferSize = 128 * 1024;

    public async Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<FFmpegPackageDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var source = ValidateSourceUri(url);
        var fullDestination = Path.GetFullPath(destinationPath);
        var partialPath = fullDestination + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        TryDelete(partialPath);
        TryDelete(fullDestination);

        Exception? lastError = null;
        try
        {
            for (var attempt = 0; attempt <= options.Value.MaximumRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await DownloadAttemptAsync(source, partialPath, progress, cancellationToken).ConfigureAwait(false);
                    File.Move(partialPath, fullDestination, overwrite: false);
                    return;
                }
                catch (Exception exception) when (IsTransient(exception, cancellationToken) && attempt < options.Value.MaximumRetries)
                {
                    lastError = exception;
                    TryDelete(partialPath);
                    var delay = TimeSpan.FromMilliseconds(Math.Min(5_000, 500 * Math.Pow(2, attempt)));
                    logger.LogWarning("FFmpeg package download attempt {Attempt} failed; retrying", attempt + 1);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsTransient(exception, cancellationToken))
                {
                    lastError = exception;
                    break;
                }
            }
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }

        TryDelete(partialPath);
        throw new AppException(
            AppErrorCode.FfmpegDownloadError,
            "Không thể tải gói FFmpeg sau số lần thử cho phép.",
            lastError);
    }

    private async Task DownloadAttemptAsync(
        Uri source,
        string partialPath,
        IProgress<FFmpegPackageDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(options.Value.DownloadTimeoutMinutes));
        HttpResponseMessage? response = null;
        try
        {
            var current = source;
            for (var redirect = 0; redirect <= 3; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
                {
                    if (redirect == 3)
                    {
                        throw new AppException(
                            AppErrorCode.FfmpegSourceUnavailable,
                            "Nguồn tải FFmpeg chuyển hướng quá nhiều lần.");
                    }

                    var redirected = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                    response.Dispose();
                    response = null;
                    current = ValidateSourceUri(redirected.AbsoluteUri);
                    continue;
                }

                break;
            }

            if (response is null)
            {
                throw new AppException(AppErrorCode.FfmpegSourceUnavailable, "Nguồn tải FFmpeg không phản hồi.");
            }

            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
            {
                throw new HttpRequestException("Transient FFmpeg package server error.", null, response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AppException(
                    AppErrorCode.FfmpegSourceUnavailable,
                    $"Nguồn tải FFmpeg trả về HTTP {(int)response.StatusCode}.");
            }

            var totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes > options.Value.MaximumDownloadBytes)
            {
                throw new AppException(
                    AppErrorCode.FfmpegDownloadError,
                    "Gói FFmpeg vượt quá giới hạn dung lượng cho phép.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var output = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long downloaded = 0;
                var stopwatch = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    downloaded += read;
                    if (downloaded > options.Value.MaximumDownloadBytes)
                    {
                        throw new AppException(
                            AppErrorCode.FfmpegDownloadError,
                            "Gói FFmpeg vượt quá giới hạn dung lượng cho phép.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    if (stopwatch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200))
                    {
                        progress?.Report(new FFmpegPackageDownloadProgress(
                            downloaded,
                            totalBytes,
                            downloaded / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001)));
                        lastReport = stopwatch.Elapsed;
                    }
                }

                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                progress?.Report(new FFmpegPackageDownloadProgress(
                    downloaded,
                    totalBytes ?? downloaded,
                    downloaded / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001)));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AppException(AppErrorCode.FfmpegDownloadError, "Tải FFmpeg vượt quá thời gian cho phép.");
        }
        finally
        {
            response?.Dispose();
        }
    }

    private Uri ValidateSourceUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !options.Value.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException(
                AppErrorCode.FfmpegSourceUnavailable,
                "Nguồn tải FFmpeg không nằm trong danh sách được phép.");
        }

        return uri;
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && exception switch
        {
            IOException => true,
            HttpRequestException { StatusCode: null } => true,
            HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests } => true,
            HttpRequestException httpException when (int?)httpException.StatusCode >= 500 => true,
            _ => false
        };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A later file operation will surface the actionable error.
        }
    }
}
