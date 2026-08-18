using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using SubVid.App.Core;
using SubVid.App.Media;

namespace SubVid.App.Tests;

public sealed class FfmpegRuntimeProvisionerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SUBVID_TESTS", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnsureReadyAsync_InstallsVerifiedToolsAndPublishesProgress()
    {
        var archive = CreateArchive(includeFfmpeg: true, includeFfprobe: true);
        var package = CreatePackage(archive);
        var paths = CreatePaths();
        var updates = new List<FfmpegInstallProgress>();
        using var http = new HttpClient(new StaticResponseHandler(archive));
        using var provisioner = new FfmpegRuntimeProvisioner(
            paths,
            http,
            package,
            (_, _, _) => Task.CompletedTask);

        var status = await provisioner.EnsureReadyAsync(
            new InlineProgress<FfmpegInstallProgress>(updates.Add),
            force: false,
            CancellationToken.None);

        Assert.True(status.Ready);
        Assert.True(status.Managed);
        Assert.Equal(package.Version, status.Version);
        Assert.Equal("MANAGED", status.Source);
        Assert.True(File.Exists(Path.Combine(provisioner.ManagedDirectory, "ffmpeg.exe")));
        Assert.True(File.Exists(Path.Combine(provisioner.ManagedDirectory, "ffprobe.exe")));
        Assert.True(File.Exists(Path.Combine(provisioner.ManagedDirectory, "THIRD-PARTY-NOTICE.txt")));
        Assert.Contains(updates, item => item.Phase == "DOWNLOAD" && item.Percent > 0);
        Assert.Equal("READY", updates[^1].Phase);
        Assert.False(File.Exists(Path.Combine(paths.RootDirectory, "Temp", "ffmpeg", $"ffmpeg-{package.Version}.zip.partial")));
    }

    [Fact]
    public async Task EnsureReadyAsync_InvalidChecksumPreservesExistingInstallation()
    {
        var archive = CreateArchive(includeFfmpeg: true, includeFfprobe: true);
        var paths = CreatePaths();
        using (var initialHttp = new HttpClient(new StaticResponseHandler(archive)))
        using (var initial = new FfmpegRuntimeProvisioner(
            paths,
            initialHttp,
            CreatePackage(archive),
            (_, _, _) => Task.CompletedTask))
        {
            _ = await initial.EnsureReadyAsync(null, force: false, CancellationToken.None);
        }

        var original = await File.ReadAllBytesAsync(Path.Combine(paths.RootDirectory, "Tools", "ffmpeg", "ffmpeg.exe"));
        var invalidPackage = CreatePackage(archive) with { ArchiveSha256 = new string('0', 64) };
        using var invalidHttp = new HttpClient(new StaticResponseHandler(archive));
        using var provisioner = new FfmpegRuntimeProvisioner(
            paths,
            invalidHttp,
            invalidPackage,
            (_, _, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<FfmpegRuntimeException>(() =>
            provisioner.EnsureReadyAsync(null, force: true, CancellationToken.None));

        Assert.Equal("FFMPEG_HASH_INVALID", exception.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(provisioner.ManagedDirectory, "ffmpeg.exe")));
        Assert.True(provisioner.GetStatus().Ready);
    }

    [Fact]
    public async Task EnsureReadyAsync_MissingFfprobeRejectsArchiveWithoutReplacingTools()
    {
        var archive = CreateArchive(includeFfmpeg: true, includeFfprobe: false);
        var paths = CreatePaths();
        using var http = new HttpClient(new StaticResponseHandler(archive));
        using var provisioner = new FfmpegRuntimeProvisioner(
            paths,
            http,
            CreatePackage(archive),
            (_, _, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<FfmpegRuntimeException>(() =>
            provisioner.EnsureReadyAsync(null, force: false, CancellationToken.None));

        Assert.Equal("FFMPEG_ARCHIVE_INVALID", exception.Code);
        Assert.False(Directory.Exists(provisioner.ManagedDirectory));
    }

    [Fact]
    public void UseExternalDirectory_PersistsAndLocatorUsesBothTools()
    {
        var paths = CreatePaths();
        var external = Path.Combine(_root, "external-ffmpeg");
        Directory.CreateDirectory(external);
        File.WriteAllBytes(Path.Combine(external, "ffmpeg.exe"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(external, "ffprobe.exe"), [4, 5, 6]);
        using (var provisioner = new FfmpegRuntimeProvisioner(paths))
        {
            var selected = provisioner.UseExternalDirectory(external);
            Assert.True(selected.Ready);
            Assert.Equal("CUSTOM", selected.Source);
            Assert.False(selected.Managed);
        }

        Assert.Equal(
            Path.Combine(external, "ffmpeg.exe"),
            MediaToolLocator.Locate(paths, "ffmpeg", "SUBVID_FFMPEG_PATH"));
        Assert.Equal(
            Path.Combine(external, "ffprobe.exe"),
            MediaToolLocator.Locate(paths, "ffprobe", "SUBVID_FFPROBE_PATH"));
    }

    [Fact]
    public async Task EnsureReadyAsync_TransientDownloadFailureCanRetryCleanly()
    {
        var archive = CreateArchive(includeFfmpeg: true, includeFfprobe: true);
        var paths = CreatePaths();
        var handler = new FlakyResponseHandler(archive);
        using var http = new HttpClient(handler);
        using var provisioner = new FfmpegRuntimeProvisioner(
            paths,
            http,
            CreatePackage(archive),
            (_, _, _) => Task.CompletedTask);

        var first = await Assert.ThrowsAsync<FfmpegRuntimeException>(() =>
            provisioner.EnsureReadyAsync(null, force: false, CancellationToken.None));
        var status = await provisioner.EnsureReadyAsync(null, force: false, CancellationToken.None);

        Assert.Equal("FFMPEG_INSTALL_FAILED", first.Code);
        Assert.True(status.Ready);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenCancelledRemovesPartialAndStagingFiles()
    {
        var archive = CreateArchive(includeFfmpeg: true, includeFfprobe: true);
        var paths = CreatePaths();
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new CancellingResponseHandler(archive, cancellation));
        using var provisioner = new FfmpegRuntimeProvisioner(
            paths,
            http,
            CreatePackage(archive),
            (_, _, _) => Task.CompletedTask);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provisioner.EnsureReadyAsync(null, force: false, cancellation.Token));

        var partial = Path.Combine(paths.RootDirectory, "Temp", "ffmpeg", "ffmpeg-test-9.0.1.zip.partial");
        var installParent = Path.Combine(paths.RootDirectory, "Tools");
        Assert.False(File.Exists(partial));
        Assert.Empty(Directory.Exists(installParent)
            ? Directory.GetDirectories(installParent, ".ffmpeg-staging-*")
            : []);
    }

    private AppPaths CreatePaths() => new(_root, Path.Combine(_root, "Models"));

    private static FfmpegRuntimePackage CreatePackage(byte[] content) => new(
        "test-9.0.1",
        new Uri("https://www.gyan.dev/ffmpeg/builds/packages/test.zip"),
        content.LongLength,
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
        "GPL-3.0",
        new Uri("https://github.com/FFmpeg/FFmpeg"));

    private static byte[] CreateArchive(bool includeFfmpeg, bool includeFfprobe)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (includeFfmpeg)
            {
                WriteEntry(archive, "ffmpeg-test/bin/ffmpeg.exe", [1, 3, 5, 7]);
            }

            if (includeFfprobe)
            {
                WriteEntry(archive, "ffmpeg-test/bin/ffprobe.exe", [2, 4, 6, 8]);
            }
        }

        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class StaticResponseHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(content),
        });
    }

    private sealed class FlakyResponseHandler(byte[] content) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(RequestCount == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(content),
                });
        }
    }

    private sealed class CancellingResponseHandler(byte[] content, CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(new CancellingStream(content, cancellation)),
        });
    }

    private sealed class CancellingStream(byte[] content, CancellationTokenSource cancellation) : MemoryStream(content)
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read > 0) cancellation.Cancel();
            return read;
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
