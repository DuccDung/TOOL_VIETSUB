using System.Text;
using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.Subtitles;

namespace TOOL_VIETSUB_APP.Tests;

public sealed class SrtServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TOOL_VIETSUB_TESTS", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParseAndSerialize_PreservesUnicodeAndMultilineText()
    {
        const string srt = "1\r\n00:00:01,250 --> 00:00:03,900\r\nXin chào thế giới.\r\nDòng thứ hai.\r\n\r\n2\r\n25:01:02.003 --> 25:01:04.010\r\n日本語テスト";

        var cues = SrtService.Parse(srt);
        var serialized = SrtService.Serialize(cues);
        var reparsed = SrtService.Parse(serialized);

        Assert.Equal(2, cues.Count);
        Assert.Equal(1_250, cues[0].StartMilliseconds);
        Assert.Equal("Xin chào thế giới.\nDòng thứ hai.", cues[0].OriginalText);
        Assert.Equal(cues[1].StartMilliseconds, reparsed[1].StartMilliseconds);
        Assert.Equal("日本語テスト", reparsed[1].OriginalText);
    }

    [Fact]
    public void Parse_RejectsEndBeforeStart()
    {
        const string invalid = "1\n00:00:05,000 --> 00:00:04,000\nSai timestamp";

        var exception = Assert.Throws<SrtException>(() => SrtService.Parse(invalid));

        Assert.Equal("SRT_TIMELINE_INVALID", exception.Code);
    }

    [Fact]
    public async Task ImportUpdateAndExport_PersistsVietnameseTranslation()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "SRT workflow");
        var sourcePath = Path.Combine(_root, "source.srt");
        await File.WriteAllTextAsync(
            sourcePath,
            "1\n00:00:00,500 --> 00:00:02,000\nWelcome.\n",
            new UTF8Encoding(false));
        var service = new SrtService(paths, workspace);

        var track = await service.ImportAsync(project, sourcePath, "en", CancellationToken.None);
        var cue = Assert.Single(track.Cues);
        project.AudioTracks.Add(new LocalMediaReference { Role = "VOICE_CUE", CueId = cue.CueId });
        await service.UpdateCueAsync(
            project,
            cue.CueId,
            "Welcome.",
            "Chào mừng bạn.",
            CancellationToken.None);
        var destination = Path.Combine(_root, "output.srt");
        await service.ExportAsync(project, destination, CancellationToken.None);

        var exported = await File.ReadAllTextAsync(destination, Encoding.UTF8);
        Assert.Contains("Chào mừng bạn.", exported);
        Assert.DoesNotContain("Welcome.", exported);
        Assert.False(File.Exists(destination + ".tmp"));
        Assert.Single(project.SubtitleTracks);
        Assert.True(cue.OriginalLocked);
        Assert.True(cue.TranslationLocked);
        Assert.DoesNotContain(project.AudioTracks, item => item.CueId == cue.CueId);
    }

    [Fact]
    public async Task TimelineEdits_AreValidatedPersistedAndInvalidateStaleVoice()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Timeline edit");
        project.SourceVideo = new LocalMediaReference
        {
            Metadata = new MediaMetadata { DurationSeconds = 10 },
        };
        var cue = new SubtitleCue
        {
            StartMilliseconds = 0,
            EndMilliseconds = 4_000,
            OriginalText = "one two three four",
            TranslatedText = "một hai ba bốn",
        };
        var track = new SubtitleDocument { Cues = [cue] };
        project.SubtitleTracks.Add(track);
        project.AudioTracks.Add(new LocalMediaReference { Role = "VOICE_CUE", CueId = cue.CueId });
        var service = new SrtService(paths, workspace);

        await service.SplitCueAsync(project, cue.CueId, 2_000, CancellationToken.None);

        Assert.Equal(2, track.Cues.Count);
        Assert.Equal("one two", track.Cues[0].OriginalText);
        Assert.Equal("three four", track.Cues[1].OriginalText);
        Assert.True(track.Cues[0].OriginalLocked);
        Assert.DoesNotContain(project.AudioTracks, item => item.CueId == cue.CueId);

        var right = track.Cues[1];
        await service.AlignCueStartAsync(project, right.CueId, 2_500, CancellationToken.None);
        var duplicateId = await service.DuplicateCueAsync(project, right.CueId, CancellationToken.None);
        await service.DeleteCueAsync(project, cue.CueId, CancellationToken.None);

        Assert.Equal(2_500, right.StartMilliseconds);
        Assert.Equal(2, track.Cues.Count);
        Assert.Contains(track.Cues, item => item.CueId == duplicateId);
        var reopened = await workspace.OpenAsync(project.ProjectId);
        Assert.Equal(2, Assert.Single(reopened.SubtitleTracks).Cues.Count);
    }

    [Fact]
    public async Task SplitCue_RejectsPlayheadTooCloseToAnEdge()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Timeline guard");
        var cue = new SubtitleCue
        {
            StartMilliseconds = 1_000,
            EndMilliseconds = 2_000,
            OriginalText = "Hello world",
        };
        project.SubtitleTracks.Add(new SubtitleDocument { Cues = [cue] });
        var service = new SrtService(paths, workspace);

        var exception = await Assert.ThrowsAsync<SrtException>(() =>
            service.SplitCueAsync(project, cue.CueId, 1_050, CancellationToken.None));

        Assert.Equal("SUBTITLE_SPLIT_POSITION_INVALID", exception.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
