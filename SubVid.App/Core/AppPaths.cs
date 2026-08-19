namespace SubVid.App.Core;

public sealed class AppPaths
{
    public const string ProductDataDirectoryName = "SubVid";
    internal const string LegacyProductDataDirectoryName = "TOOL_VIETSUB";

    public AppPaths(
        string? rootDirectory = null,
        string? modelsDirectory = null,
        string? aiRootDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? ResolveDefaultRootDirectory());
        ProjectsDirectory = Path.Combine(RootDirectory, "Projects");
        LogsDirectory = Path.Combine(RootDirectory, "Logs");
        CacheDirectory = Path.Combine(RootDirectory, "Cache");

        var configuredAiRoot = aiRootDirectory
            ?? Environment.GetEnvironmentVariable("SUBVID_AI_ROOT")
            ?? (rootDirectory is null ? AiStorageSettingsStore.TryLoad(RootDirectory)?.AiRootPath : null);
        ApplyAiRoot(
            string.IsNullOrWhiteSpace(configuredAiRoot) ? RootDirectory : configuredAiRoot,
            useLegacyLayout: string.IsNullOrWhiteSpace(configuredAiRoot));

        var configuredModelDirectory = modelsDirectory
            ?? Environment.GetEnvironmentVariable("SUBVID_MODEL_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredModelDirectory))
        {
            ModelsDirectory = Path.GetFullPath(configuredModelDirectory);
        }

        EnsureDirectories();
    }

    public string RootDirectory { get; }

    public string ProjectsDirectory { get; }

    public string LogsDirectory { get; }

    public string CacheDirectory { get; }

    public string AiRootDirectory { get; private set; } = string.Empty;

    public string ToolsDirectory { get; private set; } = string.Empty;

    public string VieNeuRuntimeDirectory { get; private set; } = string.Empty;

    public string ModelsDirectory { get; private set; } = string.Empty;

    public string AiCacheDirectory { get; private set; } = string.Empty;

    public string AiTempDirectory { get; private set; } = string.Empty;

    public bool UsesLegacyAiLayout { get; private set; }

    public static string ResolveDefaultRootDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var currentDirectory = Path.Combine(localAppData, ProductDataDirectoryName);
        var legacyDirectory = Path.Combine(localAppData, LegacyProductDataDirectoryName);
        return Directory.Exists(currentDirectory) || !Directory.Exists(legacyDirectory)
            ? currentDirectory
            : legacyDirectory;
    }

    public void ApplyAiRoot(string aiRootDirectory, bool useLegacyLayout = false)
    {
        var resolved = Path.GetFullPath(aiRootDirectory);
        AiRootDirectory = resolved;
        UsesLegacyAiLayout = useLegacyLayout;
        ToolsDirectory = useLegacyLayout
            ? Path.Combine(resolved, "Tools")
            : Path.Combine(resolved, "Runtimes", "Language");
        VieNeuRuntimeDirectory = useLegacyLayout
            ? Path.Combine(resolved, "Tools", "VieNeu")
            : Path.Combine(resolved, "Runtimes", "VieNeu");
        ModelsDirectory = Path.Combine(resolved, "Models");
        AiCacheDirectory = useLegacyLayout
            ? Path.Combine(resolved, "Tools", "Cache")
            : Path.Combine(resolved, "Cache");
        AiTempDirectory = useLegacyLayout
            ? Path.Combine(resolved, "Tools", "Temp")
            : Path.Combine(resolved, "Temp");
        EnsureDirectories();
    }

    public string GetProjectDirectory(Guid projectId) =>
        Path.Combine(ProjectsDirectory, projectId.ToString("N"));

    public string GetProjectPath(Guid projectId, params string[] segments)
    {
        var projectDirectory = Path.GetFullPath(GetProjectDirectory(projectId));
        var candidate = segments.Aggregate(projectDirectory, Path.Combine);
        var resolved = Path.GetFullPath(candidate);
        EnsureWithin(projectDirectory, resolved, "Đường dẫn dự án không hợp lệ.");
        return resolved;
    }

    public string GetModelPath(params string[] segments)
    {
        var modelDirectory = Path.GetFullPath(ModelsDirectory);
        var candidate = segments.Aggregate(modelDirectory, Path.Combine);
        var resolved = Path.GetFullPath(candidate);
        EnsureWithin(modelDirectory, resolved, "Đường dẫn model không hợp lệ.");
        return resolved;
    }

    public string GetCachePath(params string[] segments)
    {
        var cacheDirectory = Path.GetFullPath(CacheDirectory);
        var candidate = segments.Aggregate(cacheDirectory, Path.Combine);
        var resolved = Path.GetFullPath(candidate);
        EnsureWithin(cacheDirectory, resolved, "Đường dẫn cache không hợp lệ.");
        return resolved;
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ProjectsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        if (string.IsNullOrWhiteSpace(AiRootDirectory)) return;
        Directory.CreateDirectory(AiRootDirectory);
        Directory.CreateDirectory(ToolsDirectory);
        Directory.CreateDirectory(VieNeuRuntimeDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(AiCacheDirectory);
        Directory.CreateDirectory(AiTempDirectory);
    }

    private static void EnsureWithin(string root, string candidate, string message)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(message);
        }
    }
}
