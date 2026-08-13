using System.Security.Cryptography;
using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.Media;

namespace TOOL_VIETSUB_APP.Tests;

public sealed class MediaImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "TOOL_VIETSUB_TESTS",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CopyImport_CopiesReadOnlyFileAndPreservesSourceHash()
    {
        var paths = new AppPaths(_root);
        var projects = new ProjectWorkspaceService(paths);
        var manifest = await projects.CreateAsync(Guid.NewGuid(), "Kiểm thử nhập video");
        var sourcePath = Path.Combine(_root, "nguồn thử nghiệm.mp4");
        var content = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 13);
        await File.WriteAllBytesAsync(sourcePath, content);
        var sourceHashBefore = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var service = new MediaImportService(paths, projects, new FakeInspector());

        var media = await service.ImportAsync(
            manifest,
            sourcePath,
            MediaImportMode.Copy,
            maxVideoMinutes: 20);

        var copiedPath = paths.GetProjectPath(manifest.ProjectId, media.WorkspaceRelativePath!);
        Assert.True(File.Exists(copiedPath));
        Assert.True(File.GetAttributes(copiedPath).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(sourceHashBefore, media.Sha256);
        Assert.Equal(sourceHashBefore, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))).ToLowerInvariant());
        Assert.Equal(ProjectStates.Ready, manifest.Status);
    }

    [Fact]
    public async Task Import_RejectsVideoLongerThanPlanLimit()
    {
        var paths = new AppPaths(_root);
        var projects = new ProjectWorkspaceService(paths);
        var manifest = await projects.CreateAsync(Guid.NewGuid(), "Video quá dài");
        var sourcePath = Path.Combine(_root, "long.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        var service = new MediaImportService(
            paths,
            projects,
            new FakeInspector(durationSeconds: 1_201));

        var exception = await Assert.ThrowsAsync<MediaInspectionException>(() =>
            service.ImportAsync(manifest, sourcePath, MediaImportMode.Link, maxVideoMinutes: 20));

        Assert.Equal("MEDIA_DURATION_LIMIT_EXCEEDED", exception.Code);
    }

    [Fact]
    public async Task Import_RejectsFileOverConfiguredSizeLimit()
    {
        var paths = new AppPaths(_root);
        var projects = new ProjectWorkspaceService(paths);
        var manifest = await projects.CreateAsync(Guid.NewGuid(), "File quá lớn");
        var sourcePath = Path.Combine(_root, "large.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        var service = new MediaImportService(paths, projects, new FakeInspector(), maximumFileSizeBytes: 2);

        var exception = await Assert.ThrowsAsync<MediaInspectionException>(() =>
            service.ImportAsync(manifest, sourcePath, MediaImportMode.Link, maxVideoMinutes: 20));

        Assert.Equal("MEDIA_FILE_TOO_LARGE", exception.Code);
    }

    [Fact]
    public async Task Import_RejectsReplacingExistingSourceToProtectDerivedData()
    {
        var paths = new AppPaths(_root);
        var projects = new ProjectWorkspaceService(paths);
        var manifest = await projects.CreateAsync(Guid.NewGuid(), "Không thay video nguồn");
        var firstSource = Path.Combine(_root, "first.mp4");
        var secondSource = Path.Combine(_root, "second.mp4");
        await File.WriteAllBytesAsync(firstSource, [1, 2, 3]);
        await File.WriteAllBytesAsync(secondSource, [4, 5, 6]);
        var service = new MediaImportService(paths, projects, new FakeInspector());
        var original = await service.ImportAsync(
            manifest,
            firstSource,
            MediaImportMode.Link,
            maxVideoMinutes: 20);

        var exception = await Assert.ThrowsAsync<MediaInspectionException>(() =>
            service.ImportAsync(manifest, secondSource, MediaImportMode.Link, maxVideoMinutes: 20));

        Assert.Equal("MEDIA_SOURCE_ALREADY_IMPORTED", exception.Code);
        Assert.Same(original, manifest.SourceVideo);
        Assert.Equal(firstSource, manifest.SourceVideo!.OriginalPath);
    }

    [Fact]
    public async Task CopyImport_WhenCancelled_RemovesPartialFileAndKeepsProjectValid()
    {
        var paths = new AppPaths(_root);
        var projects = new ProjectWorkspaceService(paths);
        var manifest = await projects.CreateAsync(Guid.NewGuid(), "Hủy nhập video");
        var sourcePath = Path.Combine(_root, "cancel.mp4");
        await using (var source = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write))
        {
            source.SetLength(64L * 1024 * 1024);
        }

        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<MediaImportProgress>(value =>
        {
            if (value.BytesProcessed >= 1024 * 1024)
            {
                cancellation.Cancel();
            }
        });
        var service = new MediaImportService(paths, projects, new FakeInspector());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(
            manifest,
            sourcePath,
            MediaImportMode.Copy,
            maxVideoMinutes: 20,
            progress,
            cancellation.Token));

        Assert.Null(manifest.SourceVideo);
        Assert.False(Directory.EnumerateFiles(
            paths.GetProjectPath(manifest.ProjectId, "source"),
            "*.partial",
            SearchOption.TopDirectoryOnly).Any());
        Assert.Equal(64L * 1024 * 1024, new FileInfo(sourcePath).Length);
    }

    private sealed class FakeInspector(double durationSeconds = 60) : IMediaInspector
    {
        public Task<MediaMetadata> InspectAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaMetadata
            {
                DurationSeconds = durationSeconds,
                Width = 1920,
                Height = 1080,
                FramesPerSecond = 30,
                VideoCodec = "h264",
                AudioCodec = "aac",
                AudioTrackCount = 1,
                HasVideo = true,
                HasAudio = true,
                Container = "mov,mp4",
            });
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }
}
