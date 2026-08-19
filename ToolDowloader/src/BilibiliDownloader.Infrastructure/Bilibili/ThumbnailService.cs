using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Infrastructure.Bilibili;

public sealed class ThumbnailService(HttpClient httpClient, IRemoteUriValidator uriValidator) : IThumbnailService
{
    private const int MaximumThumbnailBytes = 8 * 1024 * 1024;

    public async Task<byte[]> DownloadAsync(string thumbnailUrl, CancellationToken cancellationToken)
    {
        var current = await uriValidator.ValidateImageAsync(thumbnailUrl, cancellationToken).ConfigureAwait(false);
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            using var response = await httpClient.GetAsync(
                current,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
            {
                var next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                current = await uriValidator.ValidateImageAsync(next.AbsoluteUri, cancellationToken).ConfigureAwait(false);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException(AppErrorCode.NetworkError, "Thumbnail không phải định dạng ảnh hợp lệ.");
            }

            if (response.Content.Headers.ContentLength > MaximumThumbnailBytes)
            {
                throw new AppException(AppErrorCode.FileTooLarge, "Thumbnail vượt quá giới hạn cho phép.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (output.Length + read > MaximumThumbnailBytes)
                {
                    throw new AppException(AppErrorCode.FileTooLarge, "Thumbnail vượt quá giới hạn cho phép.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new AppException(AppErrorCode.NetworkError, "Thumbnail chuyển hướng quá nhiều lần.");
    }
}
