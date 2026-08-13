namespace TOOL_VIETSUB_APP.Core;

public sealed class AppPaths
{
    public AppPaths(string? rootDirectory = null, string? modelsDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TOOL_VIETSUB"));
        ProjectsDirectory = Path.Combine(RootDirectory, "Projects");
        LogsDirectory = Path.Combine(RootDirectory, "Logs");
        ToolsDirectory = Path.Combine(RootDirectory, "Tools");
        var configuredModelDirectory = modelsDirectory
            ?? Environment.GetEnvironmentVariable("TOOL_VIETSUB_MODEL_ROOT");
        ModelsDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredModelDirectory)
            ? Path.Combine(RootDirectory, "Models")
            : configuredModelDirectory);

        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ProjectsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ToolsDirectory);
        Directory.CreateDirectory(ModelsDirectory);
    }

    public string RootDirectory { get; }

    public string ProjectsDirectory { get; }

    public string LogsDirectory { get; }

    public string ToolsDirectory { get; }

    public string ModelsDirectory { get; }

    public string GetProjectDirectory(Guid projectId) =>
        Path.Combine(ProjectsDirectory, projectId.ToString("N"));

    public string GetProjectPath(Guid projectId, params string[] segments)
    {
        var projectDirectory = Path.GetFullPath(GetProjectDirectory(projectId));
        var candidate = segments.Aggregate(projectDirectory, Path.Combine);
        var resolved = Path.GetFullPath(candidate);
        var prefix = projectDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? projectDirectory
            : projectDirectory + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolved, projectDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Đường dẫn dự án không hợp lệ.");
        }

        return resolved;
    }

    public string GetModelPath(params string[] segments)
    {
        var modelDirectory = Path.GetFullPath(ModelsDirectory);
        var candidate = segments.Aggregate(modelDirectory, Path.Combine);
        var resolved = Path.GetFullPath(candidate);
        var prefix = modelDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? modelDirectory
            : modelDirectory + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolved, modelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Đường dẫn model không hợp lệ.");
        }

        return resolved;
    }
}
