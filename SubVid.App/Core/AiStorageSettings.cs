using System.Security.Cryptography;
using System.Text.Json;
using SubVid.App.LocalAi;

namespace SubVid.App.Core;

public sealed record AiStorageSettings(int SchemaVersion, string AiRootPath);

public sealed record AiStorageStatus(
    string RootPath,
    long FreeBytes,
    bool UsesLegacyLocation,
    string RecommendedPath,
    string? PendingMigrationPath);

public sealed record AiStoragePendingMigration(
    int SchemaVersion,
    string SourceRoot,
    string DestinationRoot,
    DateTime StartedAtUtc);

public static class AiStorageSettingsStore
{
    private const string FileName = "storage.settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static AiStorageSettings? TryLoad(string appRoot)
    {
        try
        {
            var path = Path.Combine(appRoot, FileName);
            if (!File.Exists(path)) return null;
            var settings = JsonSerializer.Deserialize<AiStorageSettings>(File.ReadAllText(path), JsonOptions);
            return settings is { SchemaVersion: 1 } && !string.IsNullOrWhiteSpace(settings.AiRootPath)
                ? settings
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static async Task SaveAsync(
        string appRoot,
        string aiRootPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(appRoot);
        var path = Path.Combine(appRoot, FileName);
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(new AiStorageSettings(1, aiRootPath), JsonOptions),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public static class AiStorageMigrationStateStore
{
    private const string FileName = "storage.migration.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static AiStoragePendingMigration? TryLoad(string appRoot)
    {
        try
        {
            var path = Path.Combine(appRoot, FileName);
            if (!File.Exists(path)) return null;
            var state = JsonSerializer.Deserialize<AiStoragePendingMigration>(
                File.ReadAllText(path),
                JsonOptions);
            return state is { SchemaVersion: 1 }
                && !string.IsNullOrWhiteSpace(state.SourceRoot)
                && !string.IsNullOrWhiteSpace(state.DestinationRoot)
                    ? state
                    : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static async Task SaveAsync(
        string appRoot,
        AiStoragePendingMigration state,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(appRoot);
        var path = Path.Combine(appRoot, FileName);
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(state, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Delete(string appRoot)
    {
        try
        {
            var path = Path.Combine(appRoot, FileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale state file is harmless; GetStatus hides it once the source root changes.
        }
    }
}

public sealed class AiStorageService : IDisposable
{
    public const long MinimumFreeBytes = 6L * 1024 * 1024 * 1024;
    internal const string MigrationDirectoryName = ".subvid-migration";

    private const string MigrationMarkerFileName = "migration.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly AppPaths _paths;
    private readonly long _minimumFreeBytes;
    private readonly SemaphoreSlim _changeGate = new(1, 1);
    private bool _disposed;

    public AiStorageService(
        AppPaths paths,
        long minimumFreeBytes = MinimumFreeBytes)
    {
        _paths = paths;
        _minimumFreeBytes = minimumFreeBytes;
    }

    public bool IsChangeInProgress => _changeGate.CurrentCount == 0;

    public AiStorageStatus GetStatus()
    {
        var root = Path.GetPathRoot(_paths.AiRootDirectory);
        var freeBytes = root is null ? 0 : new DriveInfo(root).AvailableFreeSpace;
        var pending = AiStorageMigrationStateStore.TryLoad(_paths.RootDirectory);
        if (pending is not null
            && string.Equals(pending.DestinationRoot, _paths.AiRootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            AiStorageMigrationStateStore.Delete(_paths.RootDirectory);
            pending = null;
        }
        return new AiStorageStatus(
            _paths.AiRootDirectory,
            freeBytes,
            _paths.UsesLegacyAiLayout,
            GetRecommendedPath(),
            pending is not null
                && string.Equals(pending.SourceRoot, _paths.AiRootDirectory, StringComparison.OrdinalIgnoreCase)
                    ? pending.DestinationRoot
                    : null);
    }

    public async Task ChangeRootAsync(
        string destinationRoot,
        bool migrateExisting,
        IProgress<LocalRuntimeProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _changeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new LocalModelException(
                "AI_STORAGE_BUSY",
                "Một thao tác chuyển thư mục AI khác đang chạy.");
        }

        try
        {
            var source = new AiStorageLayout(
                _paths.AiRootDirectory,
                _paths.ToolsDirectory,
                _paths.VieNeuRuntimeDirectory,
                _paths.ModelsDirectory);
            var destination = await Task.Run(
                () => ValidateDestination(destinationRoot, source, migrateExisting),
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(destination, source.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new LocalRuntimeProgress(
                    "AI_STORAGE",
                    100,
                    "Thư mục AI đã ở đúng vị trí được chọn."));
                return;
            }

            var existingMigration = AiStorageMigrationStateStore.TryLoad(_paths.RootDirectory);
            if (existingMigration is not null
                && (!string.Equals(existingMigration.SourceRoot, source.RootPath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existingMigration.DestinationRoot, destination, StringComparison.OrdinalIgnoreCase)
                    || !migrateExisting))
            {
                throw new LocalModelException(
                    "AI_STORAGE_MIGRATION_PENDING",
                    $"Migration sang {existingMigration.DestinationRoot} còn dang dở. Hãy tiếp tục hoặc bỏ bản tạm trước khi chọn vị trí khác.");
            }

            if (migrateExisting)
            {
                await AiStorageMigrationStateStore.SaveAsync(
                    _paths.RootDirectory,
                    existingMigration ?? new AiStoragePendingMigration(
                        1,
                        source.RootPath,
                        destination,
                        DateTime.UtcNow),
                    cancellationToken).ConfigureAwait(false);
                await MigrateAsync(source, destination, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            progress?.Report(new LocalRuntimeProgress(
                "AI_STORAGE",
                94,
                "Đang lưu cấu hình vị trí AI mới."));
            await AiStorageSettingsStore.SaveAsync(
                _paths.RootDirectory,
                destination,
                cancellationToken).ConfigureAwait(false);
            _paths.ApplyAiRoot(destination);
            AiStorageMigrationStateStore.Delete(_paths.RootDirectory);
            progress?.Report(new LocalRuntimeProgress(
                "AI_STORAGE",
                100,
                "Đã chuyển vị trí lưu AI local."));
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new LocalRuntimeProgress(
                "AI_STORAGE",
                0,
                "Đã dừng chuyển thư mục AI. Vị trí cũ vẫn được sử dụng."));
            throw;
        }
        catch (LocalModelException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LocalModelException(
                "AI_STORAGE_MIGRATION_FAILED",
                "Không thể hoàn tất việc sao chép dữ liệu AI. Vị trí cũ vẫn được sử dụng.",
                exception);
        }
        finally
        {
            _changeGate.Release();
        }
    }

    public async Task DiscardPendingMigrationAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _changeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new LocalModelException(
                "AI_STORAGE_BUSY",
                "Một thao tác chuyển thư mục AI khác đang chạy.");
        }

        try
        {
            var pending = AiStorageMigrationStateStore.TryLoad(_paths.RootDirectory);
            if (pending is null) return;
            var stagingRoot = Path.Combine(pending.DestinationRoot, MigrationDirectoryName);
            if (Directory.Exists(stagingRoot))
            {
                var marker = await ReadMigrationMarkerAsync(
                    Path.Combine(stagingRoot, MigrationMarkerFileName),
                    cancellationToken).ConfigureAwait(false);
                if (marker is not { SchemaVersion: 1 }
                    || !string.Equals(marker.SourceRoot, pending.SourceRoot, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(marker.DestinationRoot, pending.DestinationRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new LocalModelException(
                        "AI_STORAGE_MIGRATION_CONFLICT",
                        "Không thể xác nhận thư mục migration dang dở để dọn dẹp an toàn.");
                }

                await Task.Run(
                    () => CleanupStagingDirectory(stagingRoot, pending.DestinationRoot),
                    cancellationToken).ConfigureAwait(false);
            }

            AiStorageMigrationStateStore.Delete(_paths.RootDirectory);
        }
        finally
        {
            _changeGate.Release();
        }
    }

    private string ValidateDestination(
        string destinationRoot,
        AiStorageLayout source,
        bool migrateExisting)
    {
        string destination;
        try
        {
            destination = Path.GetFullPath(destinationRoot.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LocalModelException("AI_STORAGE_INVALID", "Thư mục lưu AI không hợp lệ.", exception);
        }

        if (string.Equals(destination, source.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            return destination;
        }

        var driveRoot = Path.GetPathRoot(destination);
        if (string.IsNullOrWhiteSpace(driveRoot)
            || string.Equals(
                destination.TrimEnd(Path.DirectorySeparatorChar),
                driveRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
            || IsWithin(_paths.ProjectsDirectory, destination)
            || IsWithin(destination, _paths.ProjectsDirectory)
            || IsWithin(source.RootPath, destination)
            || IsWithin(destination, source.RootPath))
        {
            throw new LocalModelException(
                "AI_STORAGE_INVALID",
                "Hãy chọn một thư mục riêng, ví dụ D:\\SUBVID_AI; không chọn gốc ổ đĩa, Projects hoặc thư mục nằm trong vị trí AI hiện tại.");
        }

        try
        {
            Directory.CreateDirectory(destination);
            var probe = Path.Combine(destination, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LocalModelException(
                "AI_STORAGE_NOT_WRITABLE",
                "Ứng dụng không có quyền ghi vào thư mục AI đã chọn.",
                exception);
        }

        var drive = new DriveInfo(driveRoot);
        var migrationBytes = migrateExisting ? GetMigrationSize(source) : 0;
        var required = Math.Max(
            _minimumFreeBytes,
            migrationBytes + Math.Min(_minimumFreeBytes, 2L * 1024 * 1024 * 1024));
        if (drive.AvailableFreeSpace < required)
        {
            throw new LocalModelException(
                "AI_STORAGE_SPACE_INSUFFICIENT",
                $"Thư mục đã chọn cần ít nhất {Math.Ceiling(required / (double)(1024L * 1024 * 1024))} GB trống.");
        }

        return destination;
    }

    private async Task MigrateAsync(
        AiStorageLayout source,
        string destination,
        IProgress<LocalRuntimeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stagingRoot = Path.Combine(destination, MigrationDirectoryName);
        await PrepareStagingAsync(stagingRoot, source.RootPath, destination, cancellationToken)
            .ConfigureAwait(false);

        var progressState = new MigrationProgressState(GetMigrationSize(source));
        progress?.Report(new LocalRuntimeProgress(
            "AI_STORAGE",
            5,
            "Đang chuẩn bị sao chép runtime và model AI."));

        await CopyDirectoryVerifiedAsync(
            source.LanguageRuntimePath,
            Path.Combine(stagingRoot, "Runtimes", "Language"),
            "Đang sao chép runtime ngôn ngữ",
            progressState,
            progress,
            cancellationToken,
            source.VieNeuRuntimePath).ConfigureAwait(false);
        await CopyDirectoryVerifiedAsync(
            source.VieNeuRuntimePath,
            Path.Combine(stagingRoot, "Runtimes", "VieNeu"),
            "Đang sao chép runtime VieNeu",
            progressState,
            progress,
            cancellationToken).ConfigureAwait(false);
        await CopyDirectoryVerifiedAsync(
            source.ModelsPath,
            Path.Combine(stagingRoot, "Models"),
            "Đang sao chép model AI",
            progressState,
            progress,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new LocalRuntimeProgress(
            "AI_STORAGE",
            78,
            "Đang đưa dữ liệu đã kiểm tra vào thư mục chính thức."));
        await PromoteDirectoryAsync(
            Path.Combine(stagingRoot, "Runtimes", "Language"),
            Path.Combine(destination, "Runtimes", "Language"),
            cancellationToken).ConfigureAwait(false);
        await PromoteDirectoryAsync(
            Path.Combine(stagingRoot, "Runtimes", "VieNeu"),
            Path.Combine(destination, "Runtimes", "VieNeu"),
            cancellationToken).ConfigureAwait(false);
        await PromoteDirectoryAsync(
            Path.Combine(stagingRoot, "Models"),
            Path.Combine(destination, "Models"),
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new LocalRuntimeProgress(
            "AI_STORAGE",
            86,
            "Đang kiểm tra lần cuối dữ liệu AI tại vị trí mới."));
        await VerifyDirectoryAsync(
            source.LanguageRuntimePath,
            Path.Combine(destination, "Runtimes", "Language"),
            cancellationToken,
            source.VieNeuRuntimePath).ConfigureAwait(false);
        await VerifyDirectoryAsync(
            source.VieNeuRuntimePath,
            Path.Combine(destination, "Runtimes", "VieNeu"),
            cancellationToken).ConfigureAwait(false);
        await VerifyDirectoryAsync(
            source.ModelsPath,
            Path.Combine(destination, "Models"),
            cancellationToken).ConfigureAwait(false);

        CleanupStagingDirectory(stagingRoot, destination);
        progress?.Report(new LocalRuntimeProgress(
            "AI_STORAGE",
            92,
            "Đã sao chép và kiểm tra dữ liệu AI thành công."));
    }

    private static async Task PrepareStagingAsync(
        string stagingRoot,
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(stagingRoot, MigrationMarkerFileName);
        if (Directory.Exists(stagingRoot))
        {
            if (!File.Exists(markerPath))
            {
                if (Directory.EnumerateFileSystemEntries(stagingRoot).Any())
                {
                    throw new LocalModelException(
                        "AI_STORAGE_MIGRATION_CONFLICT",
                        $"Thư mục tạm {stagingRoot} đã tồn tại nhưng không thuộc migration hiện tại.");
                }
            }
            else
            {
                var existing = await ReadMigrationMarkerAsync(markerPath, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is not { SchemaVersion: 1 }
                    || !string.Equals(existing.SourceRoot, sourceRoot, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.DestinationRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new LocalModelException(
                        "AI_STORAGE_MIGRATION_CONFLICT",
                        "Thư mục đích có một migration khác còn dang dở.");
                }

                return;
            }
        }

        Directory.CreateDirectory(stagingRoot);
        var marker = new AiStorageMigrationMarker(
            1,
            sourceRoot,
            destinationRoot,
            DateTime.UtcNow);
        var temporaryMarker = markerPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryMarker,
                JsonSerializer.Serialize(marker, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryMarker, markerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryMarker)) File.Delete(temporaryMarker);
        }
    }

    private static async Task<AiStorageMigrationMarker?> ReadMigrationMarkerAsync(
        string markerPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(markerPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<AiStorageMigrationMarker>(
                await File.ReadAllTextAsync(markerPath, cancellationToken).ConfigureAwait(false),
                JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new LocalModelException(
                "AI_STORAGE_MIGRATION_CONFLICT",
                "Thông tin migration còn dang dở không hợp lệ.",
                exception);
        }
    }

    private static async Task CopyDirectoryVerifiedAsync(
        string sourceRoot,
        string destinationRoot,
        string message,
        MigrationProgressState progressState,
        IProgress<LocalRuntimeProgress>? progress,
        CancellationToken cancellationToken,
        string? excludedRoot = null)
    {
        if (!Directory.Exists(sourceRoot)) return;
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(excludedRoot) && IsWithin(excludedRoot, sourcePath))
            {
                continue;
            }

            var relative = Path.GetRelativePath(sourceRoot, sourcePath);
            var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!IsWithin(destinationRoot, destinationPath))
            {
                throw new LocalModelException(
                    "AI_STORAGE_MIGRATION_FAILED",
                    "Phát hiện đường dẫn migration không an toàn.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            var sourceLength = new FileInfo(sourcePath).Length;
            if (!await FilesMatchAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false))
            {
                var partialPath = destinationPath + ".migration.partial";
                try
                {
                    await using (var source = new FileStream(
                        sourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(
                        partialPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (!await FilesMatchAsync(sourcePath, partialPath, cancellationToken).ConfigureAwait(false))
                    {
                        throw new LocalModelException(
                            "AI_STORAGE_MIGRATION_FAILED",
                            $"Kiểm tra dữ liệu thất bại tại {relative}.");
                    }

                    File.Move(partialPath, destinationPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(partialPath)) File.Delete(partialPath);
                }
            }

            progressState.ProcessedBytes += sourceLength;
            var percent = progressState.TotalBytes <= 0
                ? 72
                : 5 + (67 * progressState.ProcessedBytes / (double)progressState.TotalBytes);
            progress?.Report(new LocalRuntimeProgress(
                "AI_STORAGE",
                Math.Clamp(percent, 5, 72),
                $"{message}: {relative}"));
        }
    }

    private static async Task PromoteDirectoryAsync(
        string stagingRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(stagingRoot)) return;
        foreach (var stagingPath in Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(stagingRoot, stagingPath);
            var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!IsWithin(destinationRoot, destinationPath))
            {
                throw new LocalModelException(
                    "AI_STORAGE_MIGRATION_FAILED",
                    "Phát hiện đường dẫn đích không an toàn khi hoàn tất migration.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (await FilesMatchAsync(stagingPath, destinationPath, cancellationToken).ConfigureAwait(false))
            {
                File.Delete(stagingPath);
            }
            else
            {
                File.Move(stagingPath, destinationPath, overwrite: true);
            }
        }
    }

    private static async Task VerifyDirectoryAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken,
        string? excludedRoot = null)
    {
        if (!Directory.Exists(sourceRoot)) return;
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(excludedRoot) && IsWithin(excludedRoot, sourcePath))
            {
                continue;
            }

            var relative = Path.GetRelativePath(sourceRoot, sourcePath);
            var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!IsWithin(destinationRoot, destinationPath)
                || !await FilesMatchAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false))
            {
                throw new LocalModelException(
                    "AI_STORAGE_MIGRATION_FAILED",
                    $"Kiểm tra dữ liệu sau migration thất bại tại {relative}.");
            }
        }
    }

    private static async Task<bool> FilesMatchAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var source = new FileInfo(sourcePath);
        var destination = new FileInfo(destinationPath);
        if (!destination.Exists || source.Length != destination.Length) return false;
        await using var sourceStream = source.OpenRead();
        await using var destinationStream = destination.OpenRead();
        var sourceHash = await SHA256.HashDataAsync(sourceStream, cancellationToken).ConfigureAwait(false);
        var destinationHash = await SHA256.HashDataAsync(destinationStream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash);
    }

    private static void CleanupStagingDirectory(string stagingRoot, string destinationRoot)
    {
        if (!Directory.Exists(stagingRoot)) return;
        if (!IsWithin(destinationRoot, stagingRoot)
            || !File.Exists(Path.Combine(stagingRoot, MigrationMarkerFileName)))
        {
            throw new LocalModelException(
                "AI_STORAGE_MIGRATION_FAILED",
                "Không thể xác nhận thư mục tạm migration để dọn dẹp an toàn.");
        }

        Directory.Delete(stagingRoot, recursive: true);
    }

    private static long GetMigrationSize(AiStorageLayout source) =>
        GetDirectorySize(source.LanguageRuntimePath, source.VieNeuRuntimePath)
        + GetDirectorySize(source.VieNeuRuntimePath)
        + GetDirectorySize(source.ModelsPath);

    private static long GetDirectorySize(string path, string? excludedRoot = null) =>
        Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(file => string.IsNullOrWhiteSpace(excludedRoot) || !IsWithin(excludedRoot, file))
                .Sum(file => new FileInfo(file).Length)
            : 0;

    private static bool IsWithin(string root, string candidate)
    {
        var resolvedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var resolvedCandidate = Path.GetFullPath(candidate);
        return resolvedCandidate.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRecommendedPath()
    {
        var preferredDrive = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
            .OrderByDescending(drive => drive.AvailableFreeSpace)
            .FirstOrDefault();
        return Path.Combine(preferredDrive?.RootDirectory.FullName ?? "D:\\", "SUBVID_AI");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    private sealed record AiStorageLayout(
        string RootPath,
        string LanguageRuntimePath,
        string VieNeuRuntimePath,
        string ModelsPath);

    private sealed record AiStorageMigrationMarker(
        int SchemaVersion,
        string SourceRoot,
        string DestinationRoot,
        DateTime StartedAtUtc);

    private sealed class MigrationProgressState(long totalBytes)
    {
        public long TotalBytes { get; } = totalBytes;

        public long ProcessedBytes { get; set; }
    }
}
