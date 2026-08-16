using System.Text.Json;
using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Tests;

public sealed class ProjectWorkspaceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "TOOL_VIETSUB_TESTS",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateRenameListAndOpen_PreservesUnicodeProject()
    {
        var paths = new AppPaths(_root);
        var service = new ProjectWorkspaceService(paths);
        var ownerId = Guid.NewGuid();

        var created = await service.CreateAsync(ownerId, "Dự án giới thiệu sản phẩm");
        var renamed = await service.RenameAsync(created.ProjectId, "Video tiếng Nhật – Tập 01");
        var projects = await service.ListAsync(ownerId);
        var opened = await service.OpenAsync(created.ProjectId);

        Assert.Equal("Video tiếng Nhật – Tập 01", renamed.Name);
        Assert.Single(projects);
        Assert.Equal(created.ProjectId, projects[0].ProjectId);
        Assert.Equal("Video tiếng Nhật – Tập 01", opened.Name);
        Assert.False(opened.LastCleanShutdown);
    }

    [Fact]
    public async Task Open_RecoversFromValidBackupWhenMainManifestIsCorrupt()
    {
        var paths = new AppPaths(_root);
        var service = new ProjectWorkspaceService(paths);
        var created = await service.CreateAsync(Guid.NewGuid(), "Bản phục hồi");
        await service.RenameAsync(created.ProjectId, "Bản phục hồi an toàn");
        var manifestPath = paths.GetProjectPath(created.ProjectId, "project.json");
        await File.WriteAllTextAsync(manifestPath, "{ invalid json");

        var recovered = await service.OpenAsync(created.ProjectId);

        Assert.Equal(created.ProjectId, recovered.ProjectId);
        Assert.False(string.IsNullOrWhiteSpace(recovered.Name));
        Assert.StartsWith("{", await File.ReadAllTextAsync(manifestPath));
    }

    [Fact]
    public async Task SaveAndOpen_PreservesSubtitleStyleSettings()
    {
        var paths = new AppPaths(_root);
        var service = new ProjectWorkspaceService(paths);
        var created = await service.CreateAsync(Guid.NewGuid(), "Kiểu phụ đề");
        created.Settings.SubtitleStyle = new SubtitleStyleSettings
        {
            PresetId = "custom",
            FontFamily = "Tahoma",
            FontSizePercent = 5.4,
            Bold = false,
            TextColor = "#FDE047",
            OutlineColor = "#101010",
            OutlineSize = 2.3,
            ShadowSize = 1.1,
            BackgroundMode = "none",
            HorizontalAlignment = "left",
            VerticalPosition = "custom",
            PositionXPercent = 8,
            PositionYPercent = 84,
            MaxWidthPercent = 76,
            MaxLines = 3,
        };
        await service.SaveAsync(created);

        var opened = await service.OpenAsync(created.ProjectId);

        Assert.Equal("Tahoma", opened.Settings.SubtitleStyle.FontFamily);
        Assert.Equal("#FDE047", opened.Settings.SubtitleStyle.TextColor);
        Assert.Equal(5.4, opened.Settings.SubtitleStyle.FontSizePercent);
        Assert.Equal(8, opened.Settings.SubtitleStyle.PositionXPercent);
        Assert.Equal(3, opened.Settings.SubtitleStyle.MaxLines);
    }

    [Fact]
    public async Task SaveAndOpen_PreservesAudioMixSettings()
    {
        var paths = new AppPaths(_root);
        var service = new ProjectWorkspaceService(paths);
        var created = await service.CreateAsync(Guid.NewGuid(), "Thiết lập âm thanh");
        created.Settings.OriginalAudioEnabled = false;
        created.Settings.OriginalAudioVolumePercent = 42;
        created.Settings.VietnameseVoiceEnabled = true;
        created.Settings.VietnameseVoiceVolumePercent = 88;
        created.Settings.VietnameseSubtitlesEnabled = false;
        created.Settings.FlipHorizontal = true;
        created.Settings.FlipVertical = true;
        await service.SaveAsync(created);

        var opened = await service.OpenAsync(created.ProjectId);

        Assert.False(opened.Settings.OriginalAudioEnabled);
        Assert.Equal(42, opened.Settings.OriginalAudioVolumePercent);
        Assert.True(opened.Settings.VietnameseVoiceEnabled);
        Assert.Equal(88, opened.Settings.VietnameseVoiceVolumePercent);
        Assert.False(opened.Settings.VietnameseSubtitlesEnabled);
        Assert.True(opened.Settings.FlipHorizontal);
        Assert.True(opened.Settings.FlipVertical);
    }

    [Fact]
    public async Task SaveAndOpen_PreservesMultipleSubtitleRemovalRegions()
    {
        var paths = new AppPaths(_root);
        var service = new ProjectWorkspaceService(paths);
        var created = await service.CreateAsync(Guid.NewGuid(), "Nhiều vùng che");
        created.Settings.RemoveOriginalSubtitles = true;
        created.Settings.OriginalSubtitleRemovalRegions =
        [
            new SubtitleRemovalRegionSettings
            {
                Id = "subtitle",
                X = 0.05,
                Y = 0.70,
                Width = 0.90,
                Height = 0.16,
            },
            new SubtitleRemovalRegionSettings
            {
                Id = "logo",
                X = 0.72,
                Y = 0.06,
                Width = 0.20,
                Height = 0.10,
            },
        ];
        await service.SaveAsync(created);

        var opened = await service.OpenAsync(created.ProjectId);

        Assert.True(opened.Settings.RemoveOriginalSubtitles);
        Assert.Equal(2, opened.Settings.OriginalSubtitleRemovalRegions.Count);
        Assert.Equal("subtitle", opened.Settings.OriginalSubtitleRemovalRegions[0].Id);
        Assert.Equal("logo", opened.Settings.OriginalSubtitleRemovalRegions[1].Id);
        Assert.Equal(0.72, opened.Settings.OriginalSubtitleRemovalRegions[1].X);
        Assert.Equal(0.05, opened.Settings.OriginalSubtitleRegionX);
        Assert.Equal(0.90, opened.Settings.OriginalSubtitleRegionWidth);
    }

    [Fact]
    public void ProjectSettings_UsesSafeAudioDefaultsForLegacyJson()
    {
        var settings = JsonSerializer.Deserialize<ProjectSettings>("{}", new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(settings);
        Assert.True(settings.OriginalAudioEnabled);
        Assert.Equal(85, settings.OriginalAudioVolumePercent);
        Assert.True(settings.VietnameseVoiceEnabled);
        Assert.Equal(100, settings.VietnameseVoiceVolumePercent);
        Assert.True(settings.VietnameseSubtitlesEnabled);
        Assert.False(settings.FlipHorizontal);
        Assert.False(settings.FlipVertical);
    }

    [Fact]
    public void GetProjectPath_RejectsTraversalOutsideWorkspace()
    {
        var paths = new AppPaths(_root);

        Assert.Throws<InvalidOperationException>(() =>
            paths.GetProjectPath(Guid.NewGuid(), "..", "..", "secret.txt"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
