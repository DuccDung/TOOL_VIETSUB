using System.Net;
using System.Security.Cryptography;
using SubVid.App.Core;
using SubVid.App.LocalAi;

namespace SubVid.App.Tests;

public sealed class LocalModelManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SUBVID_TESTS", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DownloadAsync_WritesVerifiedModelAtomically()
    {
        var content = Enumerable.Range(0, 8192).Select(index => (byte)(index % 251)).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var model = CreateDescriptor(content.LongLength, hash);
        var paths = CreatePaths();
        using var http = new HttpClient(new StaticResponseHandler(content));
        using var manager = new LocalModelManager(paths, http, [model]);

        var status = await manager.DownloadAsync(model.Id, null, CancellationToken.None);

        Assert.True(status.Ready);
        var path = paths.GetModelPath(model.Files[0].RelativePath);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task DownloadAsync_WithWrongHash_RemovesPartialAndRejectsModel()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        var model = CreateDescriptor(content.LongLength, new string('0', 64));
        var paths = CreatePaths();
        using var http = new HttpClient(new StaticResponseHandler(content));
        using var manager = new LocalModelManager(paths, http, [model]);

        var exception = await Assert.ThrowsAsync<LocalModelException>(() =>
            manager.DownloadAsync(model.Id, null, CancellationToken.None));

        Assert.Equal("MODEL_HASH_INVALID", exception.Code);
        var path = paths.GetModelPath(model.Files[0].RelativePath);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task RequireFile_RejectsTamperingEvenWhenFileSizeIsUnchanged()
    {
        var content = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var model = CreateDescriptor(content.LongLength, hash);
        var paths = CreatePaths();
        using var http = new HttpClient(new StaticResponseHandler(content));
        using var manager = new LocalModelManager(paths, http, [model]);
        _ = await manager.DownloadAsync(model.Id, null, CancellationToken.None);
        var path = paths.GetModelPath(model.Files[0].RelativePath);

        var tampered = content.Select(value => (byte)(value ^ 0x5a)).ToArray();
        await File.WriteAllBytesAsync(path, tampered);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));

        Assert.False(Assert.Single(manager.GetStatuses()).Ready);
        Assert.Throws<LocalModelException>(() => manager.RequireFile(model.Id, model.Files[0].RelativePath));
    }

    [Fact]
    public async Task DownloadAsync_WhenCancelled_RemovesPartialFile()
    {
        var content = new byte[4 * 1024 * 1024];
        var model = CreateDescriptor(content.LongLength, null);
        var paths = CreatePaths();
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new SlowResponseHandler(content, cancellation));
        using var manager = new LocalModelManager(paths, http, [model]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.DownloadAsync(model.Id, null, cancellation.Token));

        var path = paths.GetModelPath(model.Files[0].RelativePath);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".partial"));
    }

    private static LocalModelDescriptor CreateDescriptor(long size, string? hash) => new(
        "test-model",
        "TEST",
        "Test model",
        "1",
        "Test",
        [new LocalModelFile("test/model.bin", new Uri("https://huggingface.co/test/model.bin"), size, hash)]);

    private AppPaths CreatePaths() => new(_root, Path.Combine(_root, "Models"));

    private sealed class StaticResponseHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
    }

    private sealed class SlowResponseHandler(byte[] content, CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CancellingStream(content, cancellation)),
            });
    }

    private sealed class CancellingStream(byte[] content, CancellationTokenSource cancellation) : MemoryStream(content)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (Position >= 1024 * 1024)
            {
                cancellation.Cancel();
            }

            return read;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
