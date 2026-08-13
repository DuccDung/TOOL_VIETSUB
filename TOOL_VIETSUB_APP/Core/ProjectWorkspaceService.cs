using System.Collections.Concurrent;
using System.Text.Json;

namespace TOOL_VIETSUB_APP.Core;

public sealed class ProjectWorkspaceService
{
    private const string ManifestFileName = "project.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly AppPaths _paths;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _projectLocks = new();

    public ProjectWorkspaceService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<ProjectManifest> CreateAsync(
        Guid ownerUserId,
        string name,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("Tài khoản sở hữu dự án không hợp lệ.", nameof(ownerUserId));
        }

        var normalizedName = NormalizeName(name);
        var nowUtc = DateTime.UtcNow;
        var manifest = new ProjectManifest
        {
            ProjectId = projectId ?? Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = normalizedName,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            LastOpenedAtUtc = nowUtc,
            LastCleanShutdown = true,
        };

        var projectDirectory = _paths.GetProjectDirectory(manifest.ProjectId);
        if (Directory.Exists(projectDirectory))
        {
            throw new InvalidOperationException("Mã dự án đã tồn tại trên máy.");
        }

        CreateWorkspaceDirectories(manifest.ProjectId);
        await SaveAsync(manifest, cancellationToken);
        return manifest;
    }

    public async Task<ProjectManifest> OpenAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var projectLock = _projectLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await projectLock.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadBestManifestAsync(projectId, cancellationToken)
                ?? throw new FileNotFoundException("Không tìm thấy hoặc không thể phục hồi dự án.");
            manifest.RecoveryRequired = !manifest.LastCleanShutdown;
            manifest.LastOpenedAtUtc = DateTime.UtcNow;
            manifest.LastCleanShutdown = false;
            await SaveCoreAsync(manifest, cancellationToken);
            return manifest;
        }
        finally
        {
            projectLock.Release();
        }
    }

    public async Task<ProjectManifest> RenameAsync(
        Guid projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var projectLock = _projectLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await projectLock.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadBestManifestAsync(projectId, cancellationToken)
                ?? throw new FileNotFoundException("Không tìm thấy dự án.");
            manifest.Name = NormalizeName(name);
            await SaveCoreAsync(manifest, cancellationToken);
            return manifest;
        }
        finally
        {
            projectLock.Release();
        }
    }

    public async Task SaveAsync(
        ProjectManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ValidateManifest(manifest);
        var projectLock = _projectLocks.GetOrAdd(manifest.ProjectId, _ => new SemaphoreSlim(1, 1));
        await projectLock.WaitAsync(cancellationToken);
        try
        {
            await SaveCoreAsync(manifest, cancellationToken);
        }
        finally
        {
            projectLock.Release();
        }
    }

    public async Task MarkClosedAsync(
        ProjectManifest manifest,
        CancellationToken cancellationToken = default)
    {
        manifest.LastCleanShutdown = true;
        await SaveAsync(manifest, cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ProjectSummary>();
        if (!Directory.Exists(_paths.ProjectsDirectory))
        {
            return results;
        }

        foreach (var directory in Directory.EnumerateDirectories(_paths.ProjectsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var projectId))
            {
                continue;
            }

            var manifest = await LoadBestManifestAsync(projectId, cancellationToken);
            if (manifest is null || manifest.OwnerUserId != ownerUserId)
            {
                continue;
            }

            results.Add(new ProjectSummary(
                manifest.ProjectId,
                manifest.Name,
                manifest.Status,
                manifest.UpdatedAtUtc,
                !manifest.LastCleanShutdown,
                manifest.SourceVideo?.FileName,
                manifest.SourceVideo?.Metadata.DurationSeconds));
        }

        return results.OrderByDescending(item => item.UpdatedAtUtc).ToArray();
    }

    public FileStream AcquireExclusiveLock(Guid projectId)
    {
        var lockPath = _paths.GetProjectPath(projectId, "workspace.lock");
        try
        {
            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Dự án đang được mở bởi một cửa sổ TOOL VIETSUB khác.",
                exception);
        }
    }

    private async Task<ProjectManifest?> LoadBestManifestAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var manifestPath = _paths.GetProjectPath(projectId, ManifestFileName);
        var candidates = new[] { manifestPath, manifestPath + ".tmp", manifestPath + ".bak" }
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc);

        foreach (var candidate in candidates)
        {
            try
            {
                ProjectManifest? manifest;
                await using (var stream = new FileStream(
                    candidate,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    manifest = await JsonSerializer.DeserializeAsync<ProjectManifest>(
                        stream,
                        JsonOptions,
                        cancellationToken);
                }

                if (manifest is null || manifest.ProjectId != projectId)
                {
                    continue;
                }

                ValidateManifest(manifest);
                if (!string.Equals(candidate, manifestPath, StringComparison.OrdinalIgnoreCase))
                {
                    await SaveCoreAsync(manifest, cancellationToken);
                }

                return manifest;
            }
            catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
            {
                // Try the next atomic-save candidate.
            }
        }

        return null;
    }

    private async Task SaveCoreAsync(ProjectManifest manifest, CancellationToken cancellationToken)
    {
        ValidateManifest(manifest);
        CreateWorkspaceDirectories(manifest.ProjectId);
        manifest.UpdatedAtUtc = DateTime.UtcNow;

        var manifestPath = _paths.GetProjectPath(manifest.ProjectId, ManifestFileName);
        var temporaryPath = manifestPath + ".tmp";
        var backupPath = manifestPath + ".bak";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(manifestPath))
            {
                try
                {
                    File.Replace(temporaryPath, manifestPath, backupPath, ignoreMetadataErrors: true);
                }
                catch (IOException)
                {
                    File.Copy(manifestPath, backupPath, overwrite: true);
                    File.Move(temporaryPath, manifestPath, overwrite: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(manifestPath, backupPath, overwrite: true);
                    File.Move(temporaryPath, manifestPath, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, manifestPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void CreateWorkspaceDirectories(Guid projectId)
    {
        foreach (var directory in new[]
        {
            "source", "audio", "subtitles", "voice", "cache", "output", "temp", "logs",
        })
        {
            Directory.CreateDirectory(_paths.GetProjectPath(projectId, directory));
        }
    }

    private static string NormalizeName(string name)
    {
        var normalized = (name ?? string.Empty).Trim();
        if (normalized.Length is < 1 or > 120)
        {
            throw new ArgumentException("Tên dự án phải có từ 1 đến 120 ký tự.", nameof(name));
        }

        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Tên dự án chứa ký tự không hợp lệ.", nameof(name));
        }

        return normalized;
    }

    private static void ValidateManifest(ProjectManifest manifest)
    {
        if (manifest.SchemaVersion != 1
            || manifest.ProjectId == Guid.Empty
            || manifest.OwnerUserId == Guid.Empty)
        {
            throw new InvalidDataException("Manifest dự án không hợp lệ hoặc chưa được hỗ trợ.");
        }

        _ = NormalizeName(manifest.Name);
    }
}
