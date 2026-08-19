using System.Net;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Infrastructure.Configuration;
using BilibiliDownloader.Infrastructure.FFmpeg;
using BilibiliDownloader.Tests.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BilibiliDownloader.Tests.FFmpeg;

public sealed class FFmpegPackageDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_AllowedSource_StreamsToFinalFile()
    {
        using var directory = new TemporaryDirectory();
        var data = Enumerable.Range(0, 20_000).Select(index => (byte)(index % 251)).ToArray();
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, data))));
        var downloader = CreateDownloader(httpClient);
        var output = Path.Combine(directory.Path, "ffmpeg.zip");

        await downloader.DownloadAsync(
            "https://packages.test/ffmpeg.zip",
            output,
            null,
            CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(output));
        Assert.False(File.Exists(output + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_TransientServerError_Retries()
    {
        using var directory = new TemporaryDirectory();
        var calls = 0;
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? Response(HttpStatusCode.InternalServerError, [])
                : Response(HttpStatusCode.OK, [1, 2, 3]));
        }));
        var downloader = CreateDownloader(httpClient, retries: 1);

        await downloader.DownloadAsync(
            "https://packages.test/ffmpeg.zip",
            Path.Combine(directory.Path, "ffmpeg.zip"),
            null,
            CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("http://packages.test/ffmpeg.zip")]
    [InlineData("https://untrusted.test/ffmpeg.zip")]
    public async Task DownloadAsync_UntrustedSource_IsRejected(string url)
    {
        using var directory = new TemporaryDirectory();
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, [1]))));

        var exception = await Assert.ThrowsAsync<AppException>(() => CreateDownloader(httpClient).DownloadAsync(
            url,
            Path.Combine(directory.Path, "ffmpeg.zip"),
            null,
            CancellationToken.None));

        Assert.Equal("FFMPEG_SOURCE_UNAVAILABLE", exception.PublicCode);
    }

    [Fact]
    public async Task DownloadAsync_Cancelled_RemovesPartialFile()
    {
        using var directory = new TemporaryDirectory();
        using var httpClient = new HttpClient(new CallbackHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(HttpStatusCode.OK, []);
        }));
        var output = Path.Combine(directory.Path, "ffmpeg.zip");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateDownloader(httpClient).DownloadAsync(
            "https://packages.test/ffmpeg.zip",
            output,
            null,
            cancellation.Token));

        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".part"));
    }

    private static FFmpegPackageDownloader CreateDownloader(HttpClient client, int retries = 0) => new(
        client,
        Options.Create(new FFmpegOptions
        {
            AllowedHosts = ["packages.test"],
            MaximumDownloadBytes = 1_000_000,
            DownloadTimeoutMinutes = 1,
            MaximumRetries = retries
        }),
        NullLogger<FFmpegPackageDownloader>.Instance);

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] data)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(data) };
        response.Content.Headers.ContentLength = data.Length;
        return response;
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
