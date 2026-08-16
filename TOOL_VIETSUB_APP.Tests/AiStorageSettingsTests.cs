using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.LocalAi;
using System.Text.Json;

namespace TOOL_VIETSUB_APP.Tests;

public sealed class AiStorageSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "TOOL_VIETSUB_TESTS",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CustomAiRoot_UsesSeparatedRuntimeModelAndCacheLayout()
    {
        var appRoot = Path.Combine(_root, "app");
        var aiRoot = Path.Combine(_root, "ai-data");

        var paths = new AppPaths(appRoot, aiRootDirectory: aiRoot);

        Assert.Equal(Path.GetFullPath(aiRoot), paths.AiRootDirectory);
        Assert.Equal(Path.Combine(aiRoot, "Runtimes", "Language"), paths.ToolsDirectory);
        Assert.Equal(Path.Combine(aiRoot, "Runtimes", "VieNeu"), paths.VieNeuRuntimeDirectory);
        Assert.Equal(Path.Combine(aiRoot, "Models"), paths.ModelsDirectory);
        Assert.Equal(Path.Combine(aiRoot, "Cache"), paths.AiCacheDirectory);
        Assert.False(paths.UsesLegacyAiLayout);
    }

    [Fact]
    public async Task SettingsStore_WritesAtomicallyAndRoundTripsUnicodePath()
    {
        var appRoot = Path.Combine(_root, "app");
        var aiRoot = Path.Combine(_root, "Bộ nhớ AI");

        await AiStorageSettingsStore.SaveAsync(appRoot, aiRoot, CancellationToken.None);
        var loaded = AiStorageSettingsStore.TryLoad(appRoot);

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(aiRoot, loaded.AiRootPath);
        Assert.False(File.Exists(Path.Combine(appRoot, "storage.settings.json.tmp")));
    }

    [Fact]
    public async Task PendingMigrationStore_RoundTripsAndDeletesAtomically()
    {
        var appRoot = Path.Combine(_root, "pending-store");
        var state = new AiStoragePendingMigration(
            1,
            Path.Combine(_root, "nguồn"),
            Path.Combine(_root, "đích"),
            DateTime.UtcNow);

        await AiStorageMigrationStateStore.SaveAsync(appRoot, state, CancellationToken.None);
        var loaded = AiStorageMigrationStateStore.TryLoad(appRoot);

        Assert.NotNull(loaded);
        Assert.Equal(state.SourceRoot, loaded.SourceRoot);
        Assert.Equal(state.DestinationRoot, loaded.DestinationRoot);
        Assert.False(File.Exists(Path.Combine(appRoot, "storage.migration.json.tmp")));

        AiStorageMigrationStateStore.Delete(appRoot);
        Assert.Null(AiStorageMigrationStateStore.TryLoad(appRoot));
    }

    [Fact]
    public async Task Migration_CopiesAndVerifiesFilesBeforeSwitchingRoot_WithoutDeletingSource()
    {
        var appRoot = Path.Combine(_root, "legacy-app");
        var destination = Path.Combine(_root, "new-ai-root");
        var paths = new AppPaths(appRoot);
        var runtimeFile = Path.Combine(paths.ToolsDirectory, "python", "runtime.bin");
        var modelFile = paths.GetModelPath("piper", "voice.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(runtimeFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(modelFile)!);
        await File.WriteAllBytesAsync(runtimeFile, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(modelFile, [5, 6, 7, 8]);
        using var service = new AiStorageService(paths, minimumFreeBytes: 1);

        await service.ChangeRootAsync(
            destination,
            migrateExisting: true,
            progress: null,
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(destination), paths.AiRootDirectory);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(
            Path.Combine(destination, "Runtimes", "Language", "python", "runtime.bin")));
        Assert.Equal([5, 6, 7, 8], await File.ReadAllBytesAsync(
            Path.Combine(destination, "Models", "piper", "voice.onnx")));
        Assert.True(File.Exists(runtimeFile));
        Assert.True(File.Exists(modelFile));
        Assert.Equal(destination, AiStorageSettingsStore.TryLoad(appRoot)?.AiRootPath);
        Assert.Empty(Directory.EnumerateFiles(destination, "*.migration.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ChangeWithoutMigration_SwitchesAtomicallyWithoutCopyingLegacyData()
    {
        var appRoot = Path.Combine(_root, "legacy-empty-change");
        var destination = Path.Combine(_root, "empty-ai-root");
        var paths = new AppPaths(appRoot);
        var sourceFile = Path.Combine(paths.ToolsDirectory, "python", "runtime.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllBytesAsync(sourceFile, [9, 8, 7]);
        using var service = new AiStorageService(paths, minimumFreeBytes: 1);

        await service.ChangeRootAsync(
            destination,
            migrateExisting: false,
            progress: null,
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(destination), paths.AiRootDirectory);
        Assert.False(File.Exists(Path.Combine(destination, "Runtimes", "Language", "python", "runtime.bin")));
        Assert.Equal(destination, AiStorageSettingsStore.TryLoad(appRoot)?.AiRootPath);
        Assert.True(File.Exists(sourceFile));
    }

    [Fact]
    public async Task SameDestination_IsNoOpAndReportsCompletion()
    {
        var appRoot = Path.Combine(_root, "same-app");
        var aiRoot = Path.Combine(_root, "same-ai");
        var paths = new AppPaths(appRoot, aiRootDirectory: aiRoot);
        using var service = new AiStorageService(paths, minimumFreeBytes: 1);
        var updates = new List<LocalRuntimeProgress>();

        await service.ChangeRootAsync(
            aiRoot,
            migrateExisting: true,
            new InlineProgress<LocalRuntimeProgress>(updates.Add),
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(aiRoot), paths.AiRootDirectory);
        Assert.Contains(updates, update => update.Phase == "AI_STORAGE" && update.Percent == 100);
        Assert.Null(AiStorageSettingsStore.TryLoad(appRoot));
    }

    [Fact]
    public async Task DriveRoot_IsRejectedWithoutChangingConfiguration()
    {
        var appRoot = Path.Combine(_root, "invalid-root-app");
        var paths = new AppPaths(appRoot);
        var originalRoot = paths.AiRootDirectory;
        using var service = new AiStorageService(paths, minimumFreeBytes: 1);
        var driveRoot = Path.GetPathRoot(_root)!;

        var exception = await Assert.ThrowsAsync<LocalModelException>(() => service.ChangeRootAsync(
            driveRoot,
            migrateExisting: false,
            progress: null,
            CancellationToken.None));

        Assert.Equal("AI_STORAGE_INVALID", exception.Code);
        Assert.Equal(originalRoot, paths.AiRootDirectory);
        Assert.Null(AiStorageSettingsStore.TryLoad(appRoot));
    }

    [Fact]
    public async Task DestinationInsideCurrentAiRoot_IsRejected()
    {
        var appRoot = Path.Combine(_root, "nested-app");
        var paths = new AppPaths(appRoot);
        var nestedDestination = Path.Combine(paths.ModelsDirectory, "new-storage");
        using var service = new AiStorageService(paths, minimumFreeBytes: 1);

        var exception = await Assert.ThrowsAsync<LocalModelException>(() => service.ChangeRootAsync(
            nestedDestination,
            migrateExisting: true,
            progress: null,
            CancellationToken.None));

        Assert.Equal("AI_STORAGE_INVALID", exception.Code);
        Assert.Equal(Path.GetFullPath(appRoot), paths.AiRootDirectory);
        Assert.Null(AiStorageSettingsStore.TryLoad(appRoot));
    }

    [Fact]
    public async Task InsufficientSpace_DoesNotCommitNewRoot()
    {
        var appRoot = Path.Combine(_root, "space-app");
        var destination = Path.Combine(_root, "space-destination");
        var paths = new AppPaths(appRoot);
        var originalRoot = paths.AiRootDirectory;
        using var service = new AiStorageService(paths, minimumFreeBytes: long.MaxValue / 4);

        var exception = await Assert.ThrowsAsync<LocalModelException>(() => service.ChangeRootAsync(
            destination,
            migrateExisting: false,
            progress: null,
            CancellationToken.None));

        Assert.Equal("AI_STORAGE_SPACE_INSUFFICIENT", exception.Code);
        Assert.Equal(originalRoot, paths.AiRootDirectory);
        Assert.Null(AiStorageSettingsStore.TryLoad(appRoot));
    }

    [Fact]
    public async Task ExistingVerifiedStaging_IsResumedAndRemovedAfterCommit()
    {
        var appRoot = Path.Combine(_root, "resume-app");
        var destination = Path.GetFullPath(Path.Combine(_root, "resume-destination"));
        var paths = new AppPaths(appRoot);
        var sourceFile = Path.Combine(paths.ToolsDirectory, "python", "runtime.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllBytesAsync(sourceFile, [4, 3, 2, 1]);

        var stagingRoot = Path.Combine(destination, ".tool-vietsub-migration");
        var stagedFile = Path.Combine(stagingRoot, "Runtimes", "Language", "python", "runtime.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedFile)!);
        await File.WriteAllBytesAsync(stagedFile, [4, 3, 2, 1]);
        await File.WriteAllTextAsync(
            Path.Combine(stagingRoot, "migration.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                sourceRoot = paths.AiRootDirectory,
                destinationRoot = destination,
                startedAtUtc = DateTime.UtcNow,
            }));
        using var service = new AiStorageService(paths, minimumFreeBytes: 1);

        await service.ChangeRootAsync(
            destination,
            migrateExisting: true,
            progress: null,
            CancellationToken.None);

        Assert.Equal([4, 3, 2, 1], await File.ReadAllBytesAsync(
            Path.Combine(destination, "Runtimes", "Language", "python", "runtime.bin")));
        Assert.False(Directory.Exists(stagingRoot));
        Assert.Equal(destination, AiStorageSettingsStore.TryLoad(appRoot)?.AiRootPath);
    }

    [Fact]
    public async Task CancelledMigration_KeepsOldRootAndCanResumeSafely()
    {
        var appRoot = Path.Combine(_root, "cancel-app");
        var destination = Path.GetFullPath(Path.Combine(_root, "cancel-destination"));
        var paths = new AppPaths(appRoot);
        var sourceFile = Path.Combine(paths.ToolsDirectory, "python", "runtime.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllBytesAsync(sourceFile, Enumerable.Range(0, 4096).Select(value => (byte)value).ToArray());
        var originalRoot = paths.AiRootDirectory;
        using var service = new AiStorageService(paths, minimumFreeBytes: 1);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<LocalRuntimeProgress>(update =>
        {
            if (update.Phase == "AI_STORAGE" && update.Percent == 5)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ChangeRootAsync(
            destination,
            migrateExisting: true,
            progress,
            cancellation.Token));

        var stagingRoot = Path.Combine(destination, ".tool-vietsub-migration");
        Assert.Equal(originalRoot, paths.AiRootDirectory);
        Assert.Null(AiStorageSettingsStore.TryLoad(appRoot));
        Assert.True(File.Exists(Path.Combine(stagingRoot, "migration.json")));
        Assert.Empty(Directory.EnumerateFiles(stagingRoot, "*.migration.partial", SearchOption.AllDirectories));
        Assert.Equal(destination, service.GetStatus().PendingMigrationPath);

        await service.ChangeRootAsync(
            destination,
            migrateExisting: true,
            progress: null,
            CancellationToken.None);

        Assert.Equal(destination, paths.AiRootDirectory);
        Assert.False(Directory.Exists(stagingRoot));
        Assert.Null(AiStorageMigrationStateStore.TryLoad(appRoot));
        Assert.Equal(await File.ReadAllBytesAsync(sourceFile), await File.ReadAllBytesAsync(
            Path.Combine(destination, "Runtimes", "Language", "python", "runtime.bin")));
    }

    [Fact]
    public async Task DiscardPendingMigration_DeletesOnlyVerifiedStagingAndKeepsCurrentRoot()
    {
        var appRoot = Path.Combine(_root, "discard-app");
        var destination = Path.GetFullPath(Path.Combine(_root, "discard-destination"));
        var paths = new AppPaths(appRoot);
        var sourceRoot = paths.AiRootDirectory;
        var stagingRoot = Path.Combine(destination, ".tool-vietsub-migration");
        Directory.CreateDirectory(stagingRoot);
        await File.WriteAllTextAsync(
            Path.Combine(stagingRoot, "migration.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                sourceRoot,
                destinationRoot = destination,
                startedAtUtc = DateTime.UtcNow,
            }));
        await File.WriteAllTextAsync(Path.Combine(stagingRoot, "staged.bin"), "temporary");
        await AiStorageMigrationStateStore.SaveAsync(
            appRoot,
            new AiStoragePendingMigration(1, sourceRoot, destination, DateTime.UtcNow),
            CancellationToken.None);
        using var service = new AiStorageService(paths, minimumFreeBytes: 1);

        await service.DiscardPendingMigrationAsync(CancellationToken.None);

        Assert.False(Directory.Exists(stagingRoot));
        Assert.Null(AiStorageMigrationStateStore.TryLoad(appRoot));
        Assert.Equal(sourceRoot, paths.AiRootDirectory);
        Assert.Null(AiStorageSettingsStore.TryLoad(appRoot));
    }

    [Fact]
    public async Task ForeignStagingDirectory_IsNotDeletedOrCommitted()
    {
        var appRoot = Path.Combine(_root, "conflict-app");
        var destination = Path.Combine(_root, "conflict-destination");
        var paths = new AppPaths(appRoot);
        var originalRoot = paths.AiRootDirectory;
        var stagingRoot = Path.Combine(destination, ".tool-vietsub-migration");
        var foreignFile = Path.Combine(stagingRoot, "keep.txt");
        Directory.CreateDirectory(stagingRoot);
        await File.WriteAllTextAsync(foreignFile, "user data");
        using var service = new AiStorageService(paths, minimumFreeBytes: 1);

        var exception = await Assert.ThrowsAsync<LocalModelException>(() => service.ChangeRootAsync(
            destination,
            migrateExisting: true,
            progress: null,
            CancellationToken.None));

        Assert.Equal("AI_STORAGE_MIGRATION_CONFLICT", exception.Code);
        Assert.True(File.Exists(foreignFile));
        Assert.Equal(originalRoot, paths.AiRootDirectory);
        Assert.Null(AiStorageSettingsStore.TryLoad(appRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
