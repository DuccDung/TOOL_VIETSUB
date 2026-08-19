using SubVid.App.Core;
using SubVid.App.LocalAi;

namespace SubVid.App.Tests;

public sealed class VieNeuRuntimeProvisionerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SUBVID_TESTS",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void IsReady_AdoptsValidLegacyRuntimeWithoutReinstalling()
    {
        var paths = CreatePaths();
        var probeCalls = 0;
        using var provisioner = new VieNeuRuntimeProvisioner(
            paths,
            (pythonPath, timeout) =>
            {
                probeCalls++;
                Assert.Equal(Path.Combine(
                    paths.VieNeuRuntimeDirectory,
                    ".venv",
                    "Scripts",
                    "python.exe"), pythonPath);
                Assert.True(timeout >= TimeSpan.FromSeconds(15));
                return true;
            });
        CreateLegacyPython(provisioner.PythonPath);

        Assert.True(provisioner.IsReady);
        Assert.Equal(VieNeuRuntimeProvisioner.RuntimeVersion, File.ReadAllText(provisioner.MarkerPath));
        Assert.False(File.Exists(provisioner.MarkerPath + ".tmp"));
        Assert.Equal(1, probeCalls);

        Assert.True(provisioner.IsReady);
        Assert.Equal(1, probeCalls);
    }

    [Fact]
    public void IsReady_UpgradesOutdatedMarkerAfterRuntimeValidation()
    {
        var paths = CreatePaths();
        using var provisioner = new VieNeuRuntimeProvisioner(paths, (_, _) => true);
        CreateLegacyPython(provisioner.PythonPath);
        File.WriteAllText(provisioner.MarkerPath, "vieneu-legacy");

        Assert.True(provisioner.IsReady);
        Assert.Equal(VieNeuRuntimeProvisioner.RuntimeVersion, File.ReadAllText(provisioner.MarkerPath));
    }

    [Fact]
    public void IsReady_DoesNotTrustExistingFilesWhenRequiredModulesAreInvalid()
    {
        var paths = CreatePaths();
        var probeCalls = 0;
        using var provisioner = new VieNeuRuntimeProvisioner(
            paths,
            (_, _) =>
            {
                probeCalls++;
                return false;
            });
        CreateLegacyPython(provisioner.PythonPath);

        Assert.False(provisioner.IsReady);
        Assert.True(provisioner.HasExistingRuntime);
        Assert.False(File.Exists(provisioner.MarkerPath));
        Assert.Equal(1, probeCalls);

        Assert.False(provisioner.IsReady);
        Assert.Equal(1, probeCalls);
    }

    [Fact]
    public void IsReady_CurrentMarkerStillRequiresPythonExecutable()
    {
        var paths = CreatePaths();
        using var provisioner = new VieNeuRuntimeProvisioner(
            paths,
            (_, _) => throw new InvalidOperationException("Probe must not run without Python."));
        File.WriteAllText(provisioner.MarkerPath, VieNeuRuntimeProvisioner.RuntimeVersion);

        Assert.False(provisioner.IsReady);
        Assert.True(provisioner.HasExistingRuntime);
    }

    [Fact]
    public void Adoption_WritesStructuredInstallLogWithoutReprobing()
    {
        var paths = CreatePaths();
        using var provisioner = new VieNeuRuntimeProvisioner(paths, (_, _) => true);
        CreateLegacyPython(provisioner.PythonPath);

        Assert.True(provisioner.IsReady);

        var logPath = Path.Combine(paths.LogsDirectory, "voice-install.jsonl");
        var log = File.ReadAllText(logPath);
        Assert.Contains("RUNTIME_ADOPTED", log, StringComparison.Ordinal);
        Assert.Contains(paths.AiRootDirectory.Replace("\\", "\\\\"), log, StringComparison.Ordinal);
    }

    private AppPaths CreatePaths() => new(
        Path.Combine(_root, "app"),
        aiRootDirectory: Path.Combine(_root, "ai"));

    private static void CreateLegacyPython(string pythonPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);
        File.WriteAllBytes(pythonPath, [0x4d, 0x5a]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
