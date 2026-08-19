using System.Net;
using BilibiliDownloader.Infrastructure.Bilibili;
using BilibiliDownloader.Infrastructure.Configuration;
using BilibiliDownloader.Infrastructure.Download;
using BilibiliDownloader.Application.Errors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using BilibiliDownloader.Tests.Storage;

namespace BilibiliDownloader.Tests.Download;

public sealed class HttpDownloadClientTests
{
    [Fact]
    public async Task DownloadFileAsync_StreamsResponseToDisk()
    {
        using var directory = new TemporaryDirectory();
        var data = Enumerable.Range(0, 250_000).Select(index => (byte)(index % 251)).ToArray();
        using var client = CreateClient(new SequenceHandler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, data))));
        var target = Path.Combine(directory.Path, "video.part");
        var reports = new List<TransferProgress>();

        var bytes = await client.DownloadFileAsync(
            "https://media.test/video",
            target,
            1_000_000,
            0,
            TimeSpan.FromSeconds(5),
            new InlineProgress<TransferProgress>(reports.Add),
            CancellationToken.None);

        Assert.Equal(data.Length, bytes);
        Assert.Equal(data, await File.ReadAllBytesAsync(target));
        Assert.NotEmpty(reports);
    }

    [Fact]
    public async Task DownloadFileAsync_TransientFailure_Retries()
    {
        using var directory = new TemporaryDirectory();
        var calls = 0;
        using var client = CreateClient(new SequenceHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? Response(HttpStatusCode.InternalServerError, [])
                : Response(HttpStatusCode.OK, [1, 2, 3]));
        }));

        var bytes = await client.DownloadFileAsync(
            "https://media.test/video",
            Path.Combine(directory.Path, "video.part"),
            100,
            1,
            TimeSpan.FromSeconds(5),
            new InlineProgress<TransferProgress>(_ => { }),
            CancellationToken.None);

        Assert.Equal(3, bytes);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DownloadFileAsync_Cancelled_ThrowsCancellation()
    {
        using var directory = new TemporaryDirectory();
        using var client = CreateClient(new SequenceHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(HttpStatusCode.OK, []);
        }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadFileAsync(
            "https://media.test/video",
            Path.Combine(directory.Path, "video.part"),
            100,
            0,
            TimeSpan.FromSeconds(5),
            new InlineProgress<TransferProgress>(_ => { }),
            cancellation.Token));
    }

    [Fact]
    public async Task DownloadFileAsync_PermanentNetworkFailure_ReturnsNetworkError()
    {
        using var directory = new TemporaryDirectory();
        using var client = CreateClient(new SequenceHandler((_, _) =>
            throw new HttpRequestException("connection reset")));

        var exception = await Assert.ThrowsAsync<AppException>(() => client.DownloadFileAsync(
            "https://media.test/video",
            Path.Combine(directory.Path, "video.part"),
            100,
            0,
            TimeSpan.FromSeconds(5),
            new InlineProgress<TransferProgress>(_ => { }),
            CancellationToken.None));

        Assert.Equal("NETWORK_ERROR", exception.PublicCode);
    }

    private static HttpDownloadClient CreateClient(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new PassThroughUriValidator(),
        Options.Create(new DownloadOptions { ProgressIntervalMilliseconds = 1, BufferSize = 4096 }),
        NullLogger<HttpDownloadClient>.Instance);

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] data)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(data) };
        response.Content.Headers.ContentLength = data.Length;
        return response;
    }

    private sealed class SequenceHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class PassThroughUriValidator : IRemoteUriValidator
    {
        public Task<Uri> ValidateMediaAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri(url));

        public Task<Uri> ValidateImageAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri(url));
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
