using System.Net;
using System.Text.Json;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BilibiliDownloader.Infrastructure.Bilibili;

public sealed class BilibiliClient(
    HttpClient httpClient,
    IOptions<BilibiliOptions> options,
    ILogger<BilibiliClient> logger)
{
    public Task<JsonDocument> GetVideoViewAsync(string bvid, CancellationToken cancellationToken) =>
        GetJsonAsync($"x/web-interface/view?bvid={Uri.EscapeDataString(bvid)}", cancellationToken);

    public Task<JsonDocument> GetPlayUrlAsync(string bvid, long cid, CancellationToken cancellationToken) =>
        GetJsonAsync(
            $"x/player/playurl?bvid={Uri.EscapeDataString(bvid)}&cid={cid}&qn=127&fnval=4048&fourk=1",
            cancellationToken);

    private async Task<JsonDocument> GetJsonAsync(string relativeUri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                relativeUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new AppException(AppErrorCode.VideoNotFound, "Không tìm thấy video Bilibili.");
            }

            response.EnsureSuccessStatusCode();
            var maximumBytes = options.Value.MaximumResponseBytes;
            if (response.Content.Headers.ContentLength is > 0 &&
                response.Content.Headers.ContentLength > maximumBytes)
            {
                throw new AppException(AppErrorCode.VideoNotAvailable, "Phản hồi từ Bilibili vượt quá giới hạn an toàn.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var limited = new LimitedReadStream(stream, maximumBytes);
            return await JsonDocument.ParseAsync(limited, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (AppException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AppException(AppErrorCode.Timeout, "Bilibili không phản hồi trong thời gian cho phép.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Bilibili request failed for {RelativeUri}", relativeUri.Split('?')[0]);
            throw new AppException(AppErrorCode.NetworkError, "Không thể kết nối tới Bilibili.", exception);
        }
        catch (JsonException exception)
        {
            throw new AppException(AppErrorCode.VideoNotAvailable, "Bilibili trả về dữ liệu không hợp lệ.", exception);
        }
    }
}

internal sealed class LimitedReadStream(Stream inner, long maximumBytes) : Stream
{
    private long _totalRead;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _totalRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Track(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Track(read);
        return read;
    }

    private void Track(int read)
    {
        _totalRead += read;
        if (_totalRead > maximumBytes)
        {
            throw new IOException("Response exceeded the configured size limit.");
        }
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
