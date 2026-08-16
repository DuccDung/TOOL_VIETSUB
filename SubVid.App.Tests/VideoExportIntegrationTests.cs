using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SubVid.App.Core;
using SubVid.App.Jobs;
using SubVid.App.Media;

namespace SubVid.App.Tests;

public sealed class VideoExportIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SUBVID_EXPORT_TEST",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(4, "atempo=2,atempo=2")]
    [InlineData(0.25, "atempo=0.5,atempo=0.5")]
    [InlineData(1.25, "atempo=1.25")]
    public void BuildAtempo_UsesSafeFfmpegStages(double factor, string expected)
    {
        Assert.Equal(expected, VoiceTimelineJobExecutor.BuildAtempo(factor));
    }

    [Fact]
    public void BuildVideoFilter_BlursNormalizedRegionBeforeBurningVietnameseSubtitle()
    {
        var settings = new ProjectSettings
        {
            RemoveOriginalSubtitles = true,
            OriginalSubtitleRemovalMode = "blur",
            OriginalSubtitleRegionX = 0.08,
            OriginalSubtitleRegionY = 0.72,
            OriginalSubtitleRegionWidth = 0.84,
            OriginalSubtitleRegionHeight = 0.14,
        };

        var filter = VideoExportJobExecutor.BuildVideoFilter(settings, @"C:\temp\viet.ass");

        Assert.Contains("crop=w=iw*0.84:h=ih*0.14:x=iw*0.08:y=ih*0.72", filter);
        Assert.Contains("boxblur=luma_radius=min(20\\,min(h\\,w)/10):luma_power=3", filter);
        Assert.Contains("drawbox=color=black@0.22:t=fill", filter);
        Assert.Contains("overlay=x=main_w*0.08:y=main_h*0.72", filter);
        Assert.Contains("subtitles=filename='C\\:/temp/viet.ass'", filter);
        Assert.DoesNotContain("force_style", filter);
        Assert.Equal(1, filter.Split("subtitles=", StringSplitOptions.None).Length - 1);
        Assert.EndsWith("[video]", filter);
    }

    [Fact]
    public void BuildVideoFilter_BlursEveryConfiguredRemovalRegion()
    {
        var settings = new ProjectSettings
        {
            RemoveOriginalSubtitles = true,
            OriginalSubtitleRemovalMode = "blur",
            OriginalSubtitleRemovalRegions =
            [
                new SubtitleRemovalRegionSettings
                {
                    Id = "lower-third",
                    X = 0.05,
                    Y = 0.72,
                    Width = 0.90,
                    Height = 0.14,
                },
                new SubtitleRemovalRegionSettings
                {
                    Id = "watermark",
                    X = 0.72,
                    Y = 0.05,
                    Width = 0.22,
                    Height = 0.10,
                },
            ],
        };

        var filter = VideoExportJobExecutor.BuildVideoFilter(settings, @"C:\temp\viet.ass");

        Assert.Contains("crop=w=iw*0.9:h=ih*0.14:x=iw*0.05:y=ih*0.72", filter);
        Assert.Contains("crop=w=iw*0.22:h=ih*0.1:x=iw*0.72:y=ih*0.05", filter);
        Assert.Contains("[clean_video_0]split=2[video_base_1][blur_source_1]", filter);
        Assert.Equal(2, filter.Split("boxblur=", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, filter.Split("overlay=", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, filter.Split("subtitles=", StringSplitOptions.None).Length - 1);
        Assert.EndsWith("[video]", filter);
    }

    [Fact]
    public void BuildVideoFilter_CoverModeDrawsEveryConfiguredRemovalRegion()
    {
        var settings = new ProjectSettings
        {
            RemoveOriginalSubtitles = true,
            OriginalSubtitleRemovalMode = "cover",
            OriginalSubtitleRemovalRegions =
            [
                new SubtitleRemovalRegionSettings
                {
                    Id = "first",
                    X = 0.05,
                    Y = 0.70,
                    Width = 0.90,
                    Height = 0.16,
                },
                new SubtitleRemovalRegionSettings
                {
                    Id = "second",
                    X = 0.08,
                    Y = 0.08,
                    Width = 0.30,
                    Height = 0.10,
                },
            ],
        };

        var filter = VideoExportJobExecutor.BuildVideoFilter(settings, @"C:\temp\viet.ass");

        Assert.Contains("drawbox=x=iw*0.05:y=ih*0.7:w=iw*0.9:h=ih*0.16", filter);
        Assert.Contains("drawbox=x=iw*0.08:y=ih*0.08:w=iw*0.3:h=ih*0.1", filter);
        Assert.Equal(2, filter.Split("drawbox=", StringSplitOptions.None).Length - 1);
        Assert.EndsWith("[video]", filter);
    }

    [Fact]
    public void BuildVietnameseSubtitleAss_MapsStyleSortsCuesAndRejectsOverlappingLayers()
    {
        var later = new SubtitleCue
        {
            StartMilliseconds = 1200,
            EndMilliseconds = 2000,
            OriginalText = "二",
            TranslatedText = "Câu sau",
        };
        var earlier = new SubtitleCue
        {
            StartMilliseconds = 100,
            EndMilliseconds = 1000,
            OriginalText = "一",
            TranslatedText = "Câu trước",
        };

        var style = new SubtitleStyleSettings
        {
            PresetId = "custom",
            FontFamily = "Tahoma",
            FontSizePercent = 5,
            TextColor = "#FDE047",
            OutlineColor = "#101010",
            BackgroundMode = "box",
            BackgroundColor = "#020617",
            BackgroundOpacity = 68,
            HorizontalAlignment = "right",
            VerticalPosition = "top",
            PositionXPercent = 95,
            PositionYPercent = 6,
            MaxWidthPercent = 80,
            MaxLines = 2,
        };
        var serialized = VideoExportJobExecutor.BuildVietnameseSubtitleAss(
            [later, earlier], style, 720, 1280);

        Assert.Contains("PlayResX: 720", serialized);
        Assert.Contains("PlayResY: 1280", serialized);
        Assert.Contains("Style: SubVid,Tahoma,64,&H0047E0FD", serialized);
        Assert.Contains("Style: SubVidBox,Tahoma,64,&HFF47E0FD", serialized);
        Assert.Contains("&H52170602", serialized);
        Assert.Contains("{\\an9\\pos(684,76.8)}", serialized);
        Assert.Equal(4, serialized.Split("Dialogue:", StringSplitOptions.None).Length - 1);
        Assert.True(serialized.IndexOf("Câu trước", StringComparison.Ordinal)
            < serialized.IndexOf("Câu sau", StringComparison.Ordinal));
        later.StartMilliseconds = 900;
        var exception = Assert.Throws<LocalJobException>(() =>
            VideoExportJobExecutor.BuildVietnameseSubtitleAss([earlier, later], style, 720, 1280));
        Assert.Equal("SUBTITLE_TIMELINE_OVERLAP", exception.Code);
    }

    [Fact]
    public void BuildVietnameseSubtitleAss_UsesFullWidthAutomaticWordWrapping()
    {
        var cue = new SubtitleCue
        {
            StartMilliseconds = 100,
            EndMilliseconds = 2_000,
            OriginalText = "原文",
            TranslatedText = "Đây là đôi mắt giả\nmắt giả của Dominic   đấy.",
        };

        var serialized = VideoExportJobExecutor.BuildVietnameseSubtitleAss(
            [cue],
            new SubtitleStyleSettings { MaxWidthPercent = 90, MaxLines = 2 },
            1080,
            1920);

        Assert.Contains("WrapStyle: 1", serialized);
        Assert.Contains("Đây là đôi mắt giả mắt giả của Dominic đấy.", serialized);
        Assert.DoesNotContain("\\N", serialized);
    }

    [Fact]
    public void SubtitleStyleRules_RejectInvalidValuesAndNormalizeLegacySettings()
    {
        var invalid = new SubtitleStyleSettings
        {
            FontFamily = "Font không tồn tại",
            TextColor = "white",
        };

        Assert.False(SubtitleStyleRules.TryValidate(invalid, out _));
        var normalized = SubtitleStyleRules.Normalize(invalid);
        Assert.Equal("Arial", normalized.FontFamily);
        Assert.Equal("#FFFFFF", normalized.TextColor);
        Assert.Equal("readable", normalized.PresetId);
    }

    [Fact]
    public void BuildVideoFilter_CoverModeUsesDarkBoxAndRejectsOutOfBoundsRegion()
    {
        var settings = new ProjectSettings
        {
            RemoveOriginalSubtitles = true,
            OriginalSubtitleRemovalMode = "cover",
            OriginalSubtitleRegionX = 0.05,
            OriginalSubtitleRegionY = 0.70,
            OriginalSubtitleRegionWidth = 0.90,
            OriginalSubtitleRegionHeight = 0.16,
        };

        var filter = VideoExportJobExecutor.BuildVideoFilter(settings, @"C:\temp\viet.ass");
        Assert.Contains("drawbox=x=iw*0.05:y=ih*0.7:w=iw*0.9:h=ih*0.16:color=black@0.82:t=fill", filter);

        settings.OriginalSubtitleRegionWidth = 0.97;
        var exception = Assert.Throws<LocalJobException>(() =>
            VideoExportJobExecutor.BuildVideoFilter(settings, @"C:\temp\viet.ass"));
        Assert.Equal("SUBTITLE_REMOVAL_REGION_INVALID", exception.Code);
    }

    [Fact]
    public void BuildVideoFilter_HidesVietnameseSubtitleAndPassesVideoThrough()
    {
        var settings = new ProjectSettings
        {
            VietnameseSubtitlesEnabled = false,
            RemoveOriginalSubtitles = false,
        };

        var filter = VideoExportJobExecutor.BuildVideoFilter(settings, @"C:\temp\viet.ass");

        Assert.Equal("[0:v:0]null[video]", filter);
        Assert.DoesNotContain("subtitles=", filter);
    }

    [Theory]
    [InlineData(true, false, "hflip")]
    [InlineData(false, true, "vflip")]
    [InlineData(true, true, "hflip,vflip")]
    public void BuildVideoFilter_AppliesVideoFlipBeforeSubtitleLayers(
        bool flipHorizontal,
        bool flipVertical,
        string expectedTransform)
    {
        var settings = new ProjectSettings
        {
            FlipHorizontal = flipHorizontal,
            FlipVertical = flipVertical,
            RemoveOriginalSubtitles = true,
            OriginalSubtitleRemovalMode = "cover",
        };

        var filter = VideoExportJobExecutor.BuildVideoFilter(settings, @"C:\temp\viet.ass");

        var transformIndex = filter.IndexOf(expectedTransform, StringComparison.Ordinal);
        var removalIndex = filter.IndexOf("drawbox=", StringComparison.Ordinal);
        var subtitleIndex = filter.IndexOf("subtitles=", StringComparison.Ordinal);
        Assert.True(transformIndex >= 0);
        Assert.True(removalIndex > transformIndex);
        Assert.True(subtitleIndex > removalIndex);
        Assert.Contains($"[0:v:0]{expectedTransform}[transformed_video]", filter);
        Assert.EndsWith("[video]", filter);
    }

    [Fact]
    public void BuildVideoFilter_HidesVietnameseSubtitleButStillCoversOriginalSubtitle()
    {
        var settings = new ProjectSettings
        {
            VietnameseSubtitlesEnabled = false,
            RemoveOriginalSubtitles = true,
            OriginalSubtitleRemovalMode = "cover",
            OriginalSubtitleRegionX = 0.05,
            OriginalSubtitleRegionY = 0.70,
            OriginalSubtitleRegionWidth = 0.90,
            OriginalSubtitleRegionHeight = 0.16,
        };

        var filter = VideoExportJobExecutor.BuildVideoFilter(settings, @"C:\temp\viet.ass");

        Assert.Contains("drawbox=x=iw*0.05:y=ih*0.7:w=iw*0.9:h=ih*0.16", filter);
        Assert.DoesNotContain("subtitles=", filter);
        Assert.EndsWith("[video]", filter);
    }

    [Fact]
    public void BuildAudioFilter_MutesOriginalAndKeepsOnlyVietnameseVoice()
    {
        var settings = new ProjectSettings
        {
            OriginalAudioEnabled = false,
            OriginalAudioVolumePercent = 85,
            VietnameseVoiceEnabled = true,
            VietnameseVoiceVolumePercent = 72,
        };

        var filter = VideoExportJobExecutor.BuildAudioFilter(settings, sourceHasAudio: true);

        Assert.Equal("[1:a:0]aresample=48000,volume=0.72,alimiter=limit=0.95[mixed]", filter);
        Assert.DoesNotContain("[0:a:0]", filter);
        Assert.DoesNotContain("amix=", filter);
    }

    [Fact]
    public void BuildAudioFilter_UsesPersistedVolumesWhenBothTracksAreEnabled()
    {
        var settings = new ProjectSettings
        {
            OriginalAudioEnabled = true,
            OriginalAudioVolumePercent = 35,
            VietnameseVoiceEnabled = true,
            VietnameseVoiceVolumePercent = 90,
        };

        var filter = VideoExportJobExecutor.BuildAudioFilter(settings, sourceHasAudio: true);

        Assert.Contains("[0:a:0]aresample=48000,volume=0.35[background]", filter);
        Assert.Contains("[1:a:0]aresample=48000,volume=0.9,asplit=2", filter);
        Assert.Contains("sidechaincompress", filter);
        Assert.Contains("amix=inputs=2", filter);
    }

    [Fact]
    public void BuildAudioFilter_CanExportOnlyOriginalAudio()
    {
        var settings = new ProjectSettings
        {
            OriginalAudioEnabled = true,
            OriginalAudioVolumePercent = 64,
            VietnameseVoiceEnabled = false,
        };

        var filter = VideoExportJobExecutor.BuildAudioFilter(settings, sourceHasAudio: true);

        Assert.Equal("[0:a:0]aresample=48000,volume=0.64,alimiter=limit=0.95[mixed]", filter);
        Assert.DoesNotContain("[1:a:0]", filter);
    }

    [Fact]
    public void BuildAudioFilter_RejectsExportWhenNoTrackCanProduceAudio()
    {
        var settings = new ProjectSettings
        {
            OriginalAudioEnabled = true,
            VietnameseVoiceEnabled = false,
        };

        var exception = Assert.Throws<LocalJobException>(() =>
            VideoExportJobExecutor.BuildAudioFilter(settings, sourceHasAudio: false));

        Assert.Equal("AUDIO_TRACKS_DISABLED", exception.Code);
    }

    [Fact]
    public async Task FullExport_WithLocalFfmpeg_CreatesValidatedMp4WithoutChangingSource()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("SUBVID_FFMPEG_PATH");
        var ffprobe = Environment.GetEnvironmentVariable("SUBVID_FFPROBE_PATH");
        if (string.IsNullOrWhiteSpace(ffmpeg) || string.IsNullOrWhiteSpace(ffprobe)) return;
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "source.mp4");
        await RunAsync(ffmpeg,
        [
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "color=c=0x16233d:s=640x360:r=30:d=4",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4",
            "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-shortest", sourcePath,
        ]);
        var sourceBytesBefore = await File.ReadAllBytesAsync(sourcePath);
        var paths = new AppPaths(Path.Combine(_root, "app"));
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Export test");
        project.SourceVideo = new LocalMediaReference
        {
            OriginalPath = sourcePath,
            FileName = "source.mp4",
            ImportMode = "LINK",
            SizeBytes = new FileInfo(sourcePath).Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(sourceBytesBefore)).ToLowerInvariant(),
            Metadata = new MediaMetadata
            {
                DurationSeconds = 4,
                Width = 640,
                Height = 360,
                FramesPerSecond = 30,
                HasVideo = true,
                HasAudio = true,
            },
        };
        project.Settings.RemoveOriginalSubtitles = true;
        project.Settings.OriginalSubtitleRemovalMode = "blur";
        project.Settings.OriginalSubtitleRegionX = 0.05;
        project.Settings.OriginalSubtitleRegionY = 0.70;
        project.Settings.OriginalSubtitleRegionWidth = 0.90;
        project.Settings.OriginalSubtitleRegionHeight = 0.16;
        project.Settings.OriginalAudioEnabled = false;
        project.Settings.VietnameseVoiceEnabled = true;
        project.Settings.VietnameseVoiceVolumePercent = 80;
        var cues = new[]
        {
            new SubtitleCue { StartMilliseconds = 200, EndMilliseconds = 1600, OriginalText = "Hello", TranslatedText = "Xin chào" },
            new SubtitleCue { StartMilliseconds = 2100, EndMilliseconds = 3600, OriginalText = "World", TranslatedText = "Thế giới" },
        };
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "en", Cues = cues.ToList() });
        foreach (var cue in cues)
        {
            var relative = Path.Combine("voice", $"cue-{cue.CueId:N}.wav");
            var path = paths.GetProjectPath(project.ProjectId, relative);
            WriteSineWave(path, seconds: 1);
            project.AudioTracks.Add(new LocalMediaReference
            {
                CueId = cue.CueId,
                Role = "VOICE_CUE",
                ImportMode = "GENERATED",
                WorkspaceRelativePath = relative,
                FileName = Path.GetFileName(path),
                SizeBytes = new FileInfo(path).Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant(),
                Metadata = new MediaMetadata
                {
                    DurationSeconds = 1,
                    HasAudio = true,
                    AudioSampleRate = 16_000,
                    AudioChannels = 1,
                },
            });
        }

        await workspace.SaveAsync(project);
        var destination = Path.Combine(_root, "finished.mp4");
        var job = new LocalJob
        {
            JobType = "EXPORT_VIDEO_LOCAL",
            Steps =
            [
                new LocalJobStep { Code = "SYNC_VOICE" },
                new LocalJobStep { Code = "EXPORT_VIDEO" },
            ],
            Parameters = new Dictionary<string, string>
            {
                [VideoExportJobExecutor.DestinationParameter] = destination,
            },
        };
        var executor = new FullExportJobExecutor(
            new VoiceTimelineJobExecutor(paths, workspace, project, ffmpeg),
            new VideoExportJobExecutor(paths, workspace, project, ffmpeg));

        await executor.ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.True(File.Exists(destination));
        var metadata = await new FfprobeMediaInspector(paths).InspectAsync(destination, CancellationToken.None);
        Assert.True(metadata.HasVideo);
        Assert.True(metadata.HasAudio);
        Assert.InRange(metadata.DurationSeconds, 3.8, 4.2);
        Assert.Equal(sourceBytesBefore, await File.ReadAllBytesAsync(sourcePath));
        Assert.False(File.Exists(destination.Replace(".mp4", ".partial.mp4", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Cannot start FFmpeg.");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    private static void WriteSineWave(string path, int seconds)
    {
        const int sampleRate = 16_000;
        var samples = sampleRate * seconds;
        var dataSize = samples * sizeof(short);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        for (var index = 0; index < samples; index++)
        {
            writer.Write((short)(Math.Sin(2 * Math.PI * 660 * index / sampleRate) * short.MaxValue * 0.15));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
