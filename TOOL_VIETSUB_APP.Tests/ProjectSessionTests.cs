using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Tests;

public sealed class ProjectSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TOOL_VIETSUB_TESTS", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Update_AutosavesAndNormalCloseMarksCleanShutdown()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var manifest = await workspace.CreateAsync(Guid.NewGuid(), "Autosave");
        await using var session = new ProjectSession(workspace, manifest, TimeSpan.FromMilliseconds(30));
        await session.StartAsync();

        await session.UpdateAsync(project => project.Settings.OcrEnabled = true);
        await Task.Delay(100);
        var autosaved = await workspace.OpenAsync(manifest.ProjectId);
        Assert.True(autosaved.Settings.OcrEnabled);

        await session.CloseAsync();
        var projects = await workspace.ListAsync(manifest.OwnerUserId);
        Assert.False(projects[0].NeedsRecovery);
    }

    [Fact]
    public async Task Start_RejectsSecondSessionForSameProject()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var manifest = await workspace.CreateAsync(Guid.NewGuid(), "Exclusive workspace");
        await using var first = new ProjectSession(workspace, manifest);
        await first.StartAsync();
        await using var second = new ProjectSession(workspace, manifest);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync());

        Assert.Contains("đang được mở", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
