using System.Text.Json;
using System.Text.Json.Nodes;
using SubVid.App.Core;

namespace SubVid.App.Tests;

public sealed class ProjectWorkspaceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SUBVID_TESTS",
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
    public async Task SaveAndOpen_PreservesEmptyRemovalRegionsWhenRemovalIsDisabled()
    {
        var paths = new AppPaths(_root);
        var service = new ProjectWorkspaceService(paths);
        var created = await service.CreateAsync(Guid.NewGuid(), "Không che phụ đề gốc");
        created.Settings.RemoveOriginalSubtitles = false;
        created.Settings.OriginalSubtitleRemovalRegions = [];
        await service.SaveAsync(created);

        var opened = await service.OpenAsync(created.ProjectId);

        Assert.False(opened.Settings.RemoveOriginalSubtitles);
        Assert.Empty(opened.Settings.OriginalSubtitleRemovalRegions);
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
    public async Task SaveAndOpen_PreservesAdjacentVoiceBoundaryAndDropsOrphans()
    {
        var paths = new AppPaths(_root);
        var service = new ProjectWorkspaceService(paths);
        var project = await service.CreateAsync(Guid.NewGuid(), "Voice boundaries");
        var first = new SubtitleCue { StartMilliseconds = 0, EndMilliseconds = 1_000 };
        var second = new SubtitleCue { StartMilliseconds = 1_100, EndMilliseconds = 2_000 };
        project.SubtitleTracks.Add(new SubtitleDocument { Cues = [first, second] });
        project.VoicePhraseBoundaries =
        [
            new VoicePhraseBoundaryOverride
            {
                PreviousCueId = first.CueId,
                NextCueId = second.CueId,
                Mode = VoicePhraseBoundaryModes.Join,
            },
            new VoicePhraseBoundaryOverride
            {
                PreviousCueId = first.CueId,
                NextCueId = Guid.NewGuid(),
                Mode = VoicePhraseBoundaryModes.Break,
            },
        ];

        await service.SaveAsync(project);
        var reopened = await service.OpenAsync(project.ProjectId);

        var boundary = Assert.Single(reopened.VoicePhraseBoundaries);
        Assert.Equal(first.CueId, boundary.PreviousCueId);
        Assert.Equal(second.CueId, boundary.NextCueId);
        Assert.Equal(VoicePhraseBoundaryModes.Join, boundary.Mode);
    }

    [Fact]
    public async Task Save_MovesSubtitlePayloadOutOfManifestAndReopensFromDatabase()
    {
        var paths = new AppPaths(_root);
        var service = new ProjectWorkspaceService(paths);
        var project = await service.CreateAsync(Guid.NewGuid(), "Long subtitle storage");
        var cue = new SubtitleCue
        {
            StartMilliseconds = 1234,
            EndMilliseconds = 4567,
            OriginalText = "A persisted source line",
            TranslatedText = "Một câu đã lưu",
            TranslationWarnings = ["CHECK_NAME"],
        };
        project.SubtitleTracks.Add(new SubtitleDocument
        {
            LanguageCode = "en",
            Source = "WHISPER_LOCAL",
            Cues = [cue],
        });

        await service.SaveAsync(project);

        var manifestJson = await File.ReadAllTextAsync(
            paths.GetProjectPath(project.ProjectId, "project.json"));
        Assert.DoesNotContain("subtitleTracks", manifestJson, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(paths.GetProjectPath(project.ProjectId, "project.db")));

        var reopened = await new ProjectWorkspaceService(paths).OpenAsync(project.ProjectId);
        var reopenedCue = Assert.Single(Assert.Single(reopened.SubtitleTracks).Cues);
        Assert.Equal(cue.CueId, reopenedCue.CueId);
        Assert.Equal("Một câu đã lưu", reopenedCue.TranslatedText);
        Assert.Equal(["CHECK_NAME"], reopenedCue.TranslationWarnings);
    }

    [Fact]
    public async Task Open_MigratesLegacyEmbeddedSubtitleTracksToDatabase()
    {
        var paths = new AppPaths(_root);
        var service = new ProjectWorkspaceService(paths);
        var project = await service.CreateAsync(Guid.NewGuid(), "Legacy subtitle migration");
        project.SubtitleTracks.Add(new SubtitleDocument
        {
            LanguageCode = "zh",
            Source = "IMPORTED_SRT",
            Cues =
            [
                new SubtitleCue
                {
                    StartMilliseconds = 100,
                    EndMilliseconds = 900,
                    OriginalText = "旧字幕",
                    TranslatedText = "Phụ đề cũ",
                },
            ],
        });
        var manifestPath = paths.GetProjectPath(project.ProjectId, "project.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        root["subtitleTracks"] = JsonSerializer.SerializeToNode(
            project.SubtitleTracks,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllTextAsync(manifestPath, root.ToJsonString());
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var databasePath = paths.GetProjectPath(project.ProjectId, "project.db") + suffix;
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }

        var reopened = await new ProjectWorkspaceService(paths).OpenAsync(project.ProjectId);

        Assert.Equal("旧字幕", Assert.Single(Assert.Single(reopened.SubtitleTracks).Cues).OriginalText);
        Assert.True(File.Exists(paths.GetProjectPath(project.ProjectId, "project.db")));
        Assert.DoesNotContain(
            "subtitleTracks",
            await File.ReadAllTextAsync(manifestPath),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAndOpen_FiveThousandCues_KeepsManifestBounded()
    {
        var paths = new AppPaths(_root);
        var service = new ProjectWorkspaceService(paths);
        var project = await service.CreateAsync(Guid.NewGuid(), "Five thousand cues");
        var cues = Enumerable.Range(0, 5_000).Select(index => new SubtitleCue
        {
            StartMilliseconds = index * 2_000L,
            EndMilliseconds = index * 2_000L + 1_800,
            OriginalText = $"Source subtitle {index}",
            TranslatedText = $"Phụ đề {index}",
        }).ToList();
        project.SubtitleTracks.Add(new SubtitleDocument
        {
            LanguageCode = "en",
            Source = "WHISPER_LOCAL",
            Cues = cues,
        });

        await service.SaveAsync(project);
        cues[2_500].TranslatedText = "Chỉ cập nhật một cue";
        await service.SaveAsync(project);

        var manifest = new FileInfo(paths.GetProjectPath(project.ProjectId, "project.json"));
        Assert.True(manifest.Length < 100_000, manifest.Length.ToString());
        var reopened = await new ProjectWorkspaceService(paths).OpenAsync(project.ProjectId);
        var reopenedCues = Assert.Single(reopened.SubtitleTracks).Cues;
        Assert.Equal(5_000, reopenedCues.Count);
        Assert.Equal("Chỉ cập nhật một cue", reopenedCues[2_500].TranslatedText);
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
