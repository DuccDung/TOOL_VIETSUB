using System.Buffers;
using System.Diagnostics;
using System.Net;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Infrastructure.Bilibili;
using BilibiliDownloader.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BilibiliDownloader.Infrastructure.Download;

public sealed record TransferProgress(long DownloadedBytes, long? TotalBytes, double SpeedBytesPerSecond);

public interface IHttpDownloadClient
{
    Task<long> DownloadFileAsync(
        string url,
        string destinationPath,
        long maximumFileSize,
        int maximumRetries,
        TimeSpan inactivityTimeout,
        IProgress<TransferProgress> progress,
        CancellationToken cancellationToken);
}

public sealed class HttpDownloadClient(
    HttpClient httpClient,
    IRemoteUriValidator uriValidator,
    IOptions<DownloadOptions> options,
    ILogger<HttpDownloadClient> logger) : IHttpDownloadClient, IDisposable
{
    public async Task<long> DownloadFileAsync(
        string url,
        string destinationPath,
        long maximumFileSize,
        int maximumRetries,
        TimeSpan inactivityTimeout,
        IProgress<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt <= maximumRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await DownloadAttemptAsync(
                    url,
                    destinationPath,
                    maximumFileSize,
                    inactivityTimeout,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken) && attempt < maximumRetries)
            {
                lastException = exception;
                TryDelete(destinationPath);
                var delay = TimeSpan.FromMilliseconds(Math.Min(10_000, 500 * Math.Pow(2, attempt)) + Random.Shared.Next(0, 250));
                logger.LogWarning(
                    "Download attempt {Attempt} failed with {FailureType}; retrying",
                    attempt + 1,
                    exception.GetType().Name);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                lastException = exception;
                break;
            }
        }

        if (lastException is AppException appException)
        {
            throw appException;
        }

        throw new AppException(AppErrorCode.NetworkError, "Kết nối tới máy chủ media bị gián đoạn.", lastException);
    }

    private async Task<long> DownloadAttemptAsync(
        string url,
        string destinationPath,
        long maximumFileSize,
        TimeSpan inactivityTimeout,
        IProgress<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        var current = await uriValidator.ValidateMediaAsync(url, cancellationToken).ConfigureAwait(false);
        HttpResponseMessage? response = null;
        try
        {
            for (var redirect = 0; redirect <= 3; redirect++)
            {
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                headerTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.Referrer = new Uri("https://www.bilibili.com/");
                response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    headerTimeout.Token).ConfigureAwait(false);

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
                {
                    var next = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                    response.Dispose();
                    response = null;
                    current = await uriValidator.ValidateMediaAsync(next.AbsoluteUri, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                break;
            }

            if (response is null)
            {
                throw new AppException(AppErrorCode.NetworkError, "Media chuyển hướng quá nhiều lần.");
            }

            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
            {
                throw new HttpRequestException("Transient media server error.", null, response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AppException(
                    AppErrorCode.VideoNotAvailable,
                    $"Máy chủ media từ chối request (HTTP {(int)response.StatusCode}).");
            }
            var totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes > maximumFileSize)
            {
                throw new AppException(AppErrorCode.FileTooLarge, "Video vượt quá giới hạn kích thước đã cấu hình.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                options.Value.BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = ArrayPool<byte>.Shared.Rent(options.Value.BufferSize);
            try
            {
                long downloaded = 0;
                var stopwatch = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                while (true)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    readTimeout.CancelAfter(inactivityTimeout);
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), readTimeout.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    downloaded += read;
                    if (downloaded > maximumFileSize)
                    {
                        throw new AppException(AppErrorCode.FileTooLarge, "Video vượt quá giới hạn kích thước đã cấu hình.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (stopwatch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(options.Value.ProgressIntervalMilliseconds))
                    {
                        var speed = downloaded / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                        progress.Report(new TransferProgress(downloaded, totalBytes, speed));
                        lastReport = stopwatch.Elapsed;
                    }
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                progress.Report(new TransferProgress(
                    downloaded,
                    totalBytes ?? downloaded,
                    downloaded / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001)));
                return downloaded;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AppException(AppErrorCode.Timeout, "Download không nhận được dữ liệu trong thời gian cho phép.");
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception switch
        {
            IOException => true,
            HttpRequestException { StatusCode: null } => true,
            HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests } => true,
            HttpRequestException httpException when (int?)httpException.StatusCode >= 500 => true,
            AppException { Code: AppErrorCode.Timeout or AppErrorCode.NetworkError } => true,
            _ => false
        };
    }

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
            // The next attempt will surface a clear file error if the file remains locked.
        }
    }

    public void Dispose()
    {
        httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
