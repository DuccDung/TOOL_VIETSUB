using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using SubVid.App.Core;
using SubVid.App.LocalAi;
using SubVid.App.Media;
using SubVid.App.Subtitles;

namespace SubVid.App.Jobs;

public sealed class VideoExportJobExecutor : ILocalJobExecutor
{
    public const string DestinationParameter = "destinationPath";
    private readonly AppPaths _paths;
    private readonly ProjectWorkspaceService _workspace;
    private readonly ProjectManifest _project;
    private readonly string _ffmpegPath;
    private readonly FfmpegProgressRunner _runner;
    private readonly IMediaInspector _inspector;

    public VideoExportJobExecutor(
        AppPaths paths,
        ProjectWorkspaceService workspace,
        ProjectManifest project,
        string? ffmpegPath = null,
        FfmpegProgressRunner? runner = null,
        IMediaInspector? inspector = null)
    {
        _paths = paths;
        _workspace = workspace;
        _project = project;
        _ffmpegPath = ffmpegPath ?? MediaToolLocator.Locate(paths, "ffmpeg", "SUBVID_FFMPEG_PATH");
        _runner = runner ?? new FfmpegProgressRunner();
        _inspector = inspector ?? new FfprobeMediaInspector(paths);
    }

    public async Task ExecuteAsync(
        LocalJob job,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        var source = _project.SourceVideo
            ?? throw new LocalJobException("MEDIA_SOURCE_MISSING", "Dự án chưa có video nguồn.", retryable: false);
        var sourcePath = source.ImportMode == "COPY" && source.WorkspaceRelativePath is not null
            ? _paths.GetProjectPath(_project.ProjectId, source.WorkspaceRelativePath)
            : Path.GetFullPath(source.OriginalPath);
        var sourceFile = new FileInfo(sourcePath);
        if (!sourceFile.Exists || sourceFile.Length != source.SizeBytes)
        {
            throw new LocalJobException("MEDIA_SOURCE_CHANGED", "Video nguồn đã bị thay đổi hoặc xóa.", retryable: false);
        }

        if (!await HasExpectedHashAsync(sourcePath, source.Sha256, cancellationToken))
        {
            throw new LocalJobException(
                "MEDIA_SOURCE_CHANGED",
                "Ná»™i dung video nguá»“n Ä‘Ã£ bá»‹ thay Ä‘á»•i.",
                retryable: false);
        }

        var includeOriginalAudio = _project.Settings.OriginalAudioEnabled && source.Metadata.HasAudio;
        var includeVietnameseVoice = _project.Settings.VietnameseVoiceEnabled;
        var includeVietnameseSubtitles = _project.Settings.VietnameseSubtitlesEnabled;
        if (!includeOriginalAudio && !includeVietnameseVoice)
        {
            throw new LocalJobException(
                "AUDIO_TRACKS_DISABLED",
                "Hãy bật Âm gốc hoặc Giọng Việt trước khi xuất video.",
                retryable: false);
        }

        string? voicePath = null;
        if (includeVietnameseVoice)
        {
            var voice = _project.AudioTracks.LastOrDefault(item => item.Role == "VOICE_TIMELINE")
                ?? throw new LocalJobException("VOICE_TIMELINE_MISSING", "Hãy đồng bộ giọng Việt trước khi xuất video.", retryable: false);
            if (voice.WorkspaceRelativePath is null)
            {
                throw new LocalJobException("VOICE_TIMELINE_MISSING", "Thiếu file timeline giọng Việt.");
            }

            voicePath = _paths.GetProjectPath(_project.ProjectId, voice.WorkspaceRelativePath);
            if (!File.Exists(voicePath) || new FileInfo(voicePath).Length != voice.SizeBytes)
            {
                throw new LocalJobException("VOICE_TIMELINE_CHANGED", "Timeline giọng Việt đã bị thay đổi hoặc xóa.");
            }

            if (!await HasExpectedHashAsync(voicePath, voice.Sha256, cancellationToken))
            {
                throw new LocalJobException(
                    "VOICE_TIMELINE_CHANGED",
                    "Nội dung timeline giọng Việt đã bị thay đổi.");
            }
        }

        if (!job.Parameters.TryGetValue(DestinationParameter, out var configuredDestination)
            || string.IsNullOrWhiteSpace(configuredDestination))
        {
            throw new LocalJobException("EXPORT_DESTINATION_MISSING", "Chưa chọn nơi lưu video xuất.", retryable: false);
        }

        var destination = Path.GetFullPath(configuredDestination);
        if (!string.Equals(Path.GetExtension(destination), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            destination += ".mp4";
        }

        RejectSourceOverwrite(destination, source, sourcePath);
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new LocalJobException("EXPORT_DESTINATION_INVALID", "Thư mục xuất video không hợp lệ.", retryable: false);
        Directory.CreateDirectory(destinationDirectory);

        SubtitleCue[] translatedCues = [];
        if (includeVietnameseSubtitles)
        {
            var track = _project.SubtitleTracks.LastOrDefault(item => item.Cues.Count > 0)
                ?? throw new LocalJobException("SUBTITLE_TRACK_MISSING", "Chưa có phụ đề để xuất.", retryable: false);
            translatedCues = track.Cues
                .Where(cue => !string.IsNullOrWhiteSpace(cue.TranslatedText))
                .OrderBy(cue => cue.StartMilliseconds)
                .ThenBy(cue => cue.EndMilliseconds)
                .ToArray();
            if (translatedCues.Length == 0)
            {
                throw new LocalJobException(
                    "TRANSLATION_MISSING",
                    "Chưa có phụ đề tiếng Việt hợp lệ để đè lên video.",
                    retryable: false);
            }

            var invalidCue = translatedCues.FirstOrDefault(cue =>
                TranslationQualityValidator.LooksPathological(cue.OriginalText, cue.TranslatedText));
            if (invalidCue is not null)
            {
                throw new LocalJobException(
                    "TRANSLATION_QUALITY_INVALID",
                    "Còn bản dịch bị lặp hoặc dài bất thường. Hãy dịch lại lỗi trước khi xuất video.",
                    retryable: false);
            }
        }

        var subtitlePath = _paths.GetProjectPath(_project.ProjectId, "temp", $"export-{job.JobId:N}.ass");
        var renderPartial = _paths.GetProjectPath(_project.ProjectId, "temp", $"export-{job.JobId:N}.partial.mp4");
        var relativeOutput = Path.Combine("output", $"subvid-{job.JobId:N}.mp4");
        var projectOutput = _paths.GetProjectPath(_project.ProjectId, relativeOutput);
        var externalPartial = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileNameWithoutExtension(destination)}.{job.JobId:N}.partial.mp4");
        try
        {
            if (includeVietnameseSubtitles)
            {
                await File.WriteAllTextAsync(
                    subtitlePath,
                    BuildVietnameseSubtitleAss(
                        translatedCues,
                        _project.Settings.SubtitleStyle,
                        source.Metadata.Width,
                        source.Metadata.Height),
                    new UTF8Encoding(false),
                    cancellationToken);
            }
            var audioFilter = BuildAudioFilter(_project.Settings, source.Metadata.HasAudio);
            var videoFilter = BuildVideoFilter(_project.Settings, subtitlePath);
            var filter = $"{videoFilter};{audioFilter}";
            var arguments = new List<string>
            {
                "-y", "-v", "error",
                "-i", sourcePath,
            };
            if (includeVietnameseVoice)
            {
                arguments.AddRange(["-i", voicePath!]);
            }

            arguments.AddRange([
                "-filter_complex", filter,
                "-map", "[video]",
                "-map", "[mixed]",
                "-c:v", "libx264",
                "-preset", "medium",
                "-crf", "20",
                "-pix_fmt", "yuv420p",
                "-c:a", "aac",
                "-b:a", "192k",
                "-movflags", "+faststart",
                "-t", source.Metadata.DurationSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                renderPartial,
            ]);
            await reportProgress(new JobProgressUpdate(
                "EXPORT_VIDEO",
                0,
                0,
                includeVietnameseSubtitles
                    ? _project.Settings.RemoveOriginalSubtitles
                        ? "Đang che phụ đề gốc, đè phụ đề Việt và mã hóa MP4."
                        : "Đang đè phụ đề Việt, trộn âm thanh và mã hóa MP4."
                    : _project.Settings.RemoveOriginalSubtitles
                        ? "Đang che phụ đề gốc, ẩn phụ đề Việt và mã hóa MP4."
                        : "Đang xuất video không kèm phụ đề Việt."));
            await _runner.RunAsync(
                _ffmpegPath,
                arguments,
                source.Metadata.DurationSeconds,
                progress => reportProgress(new JobProgressUpdate("EXPORT_VIDEO", progress, progress)),
                cancellationToken);
            var metadata = await _inspector.InspectAsync(renderPartial, cancellationToken);
            if (!metadata.HasVideo || !metadata.HasAudio
                || metadata.DurationSeconds <= 0
                || Math.Abs(metadata.DurationSeconds - source.Metadata.DurationSeconds) > 2)
            {
                throw new LocalJobException("EXPORT_VALIDATION_FAILED", "Video xuất không vượt qua kiểm tra kỹ thuật.");
            }

            File.Move(renderPartial, projectOutput, overwrite: true);
            if (!string.Equals(projectOutput, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(projectOutput, externalPartial, overwrite: true);
                File.Move(externalPartial, destination, overwrite: true);
            }

            job.Steps.Single(item => item.Code == "EXPORT_VIDEO").OutputRelativePath = relativeOutput;
            job.Parameters[DestinationParameter] = destination;
            await _workspace.SaveAsync(_project, cancellationToken);
        }
        finally
        {
            if (File.Exists(subtitlePath)) File.Delete(subtitlePath);
            if (File.Exists(renderPartial)) File.Delete(renderPartial);
            if (File.Exists(externalPartial)) File.Delete(externalPartial);
        }
    }

    public static string BuildVideoFilter(ProjectSettings settings, string subtitlePath)
    {
        var transformFilters = new List<string>(2);
        if (settings.FlipHorizontal)
        {
            transformFilters.Add("hflip");
        }
        if (settings.FlipVertical)
        {
            transformFilters.Add("vflip");
        }

        var transformPrefix = transformFilters.Count > 0
            ? $"[0:v:0]{string.Join(',', transformFilters)}[transformed_video];"
            : string.Empty;
        var videoInput = transformFilters.Count > 0 ? "[transformed_video]" : "[0:v:0]";
        var includeVietnameseSubtitles = settings.VietnameseSubtitlesEnabled;
        var subtitleFilter = includeVietnameseSubtitles
            ? $"subtitles=filename='{EscapeFilterPath(subtitlePath)}'"
            : null;
        if (!settings.RemoveOriginalSubtitles)
        {
            return includeVietnameseSubtitles
                ? $"{transformPrefix}{videoInput}{subtitleFilter}[video]"
                : $"{transformPrefix}{videoInput}null[video]";
        }

        var removalRegions = GetEffectiveSubtitleRemovalRegions(settings);
        ValidateRemovalSettings(settings, removalRegions);
        if (string.Equals(settings.OriginalSubtitleRemovalMode, "cover", StringComparison.OrdinalIgnoreCase))
        {
            var drawBoxes = removalRegions.Select(region =>
                $"drawbox=x=iw*{Format(region.X)}:y=ih*{Format(region.Y)}:w=iw*{Format(region.Width)}:h=ih*{Format(region.Height)}:color=black@0.82:t=fill");
            var cleanVideo = $"{transformPrefix}{videoInput}{string.Join(',', drawBoxes)}";
            return includeVietnameseSubtitles
                ? $"{cleanVideo}[clean_video];[clean_video]{subtitleFilter}[video]"
                : $"{cleanVideo}[video]";
        }

        var blurredVideo = new StringBuilder(transformPrefix);
        var currentVideo = videoInput;
        for (var index = 0; index < removalRegions.Count; index++)
        {
            var region = removalRegions[index];
            var x = Format(region.X);
            var y = Format(region.Y);
            var width = Format(region.Width);
            var height = Format(region.Height);
            var isLast = index == removalRegions.Count - 1;
            var nextVideo = isLast
                ? includeVietnameseSubtitles ? "[clean_video]" : "[video]"
                : $"[clean_video_{index}]";
            blurredVideo.Append(currentVideo)
                .Append($"split=2[video_base_{index}][blur_source_{index}];")
                .Append($"[blur_source_{index}]crop=w=iw*{width}:h=ih*{height}:x=iw*{x}:y=ih*{y},")
                .Append("boxblur=luma_radius=min(20\\,min(h\\,w)/10):luma_power=3,")
                .Append($"drawbox=color=black@0.22:t=fill[blurred_region_{index}];")
                .Append($"[video_base_{index}][blurred_region_{index}]overlay=x=main_w*{x}:y=main_h*{y}")
                .Append(nextVideo);
            if (!isLast)
            {
                blurredVideo.Append(';');
            }
            currentVideo = nextVideo;
        }

        return includeVietnameseSubtitles
            ? $"{blurredVideo};[clean_video]{subtitleFilter}[video]"
            : blurredVideo.ToString();
    }

    public static string BuildAudioFilter(ProjectSettings settings, bool sourceHasAudio)
    {
        ValidateAudioSettings(settings);
        var includeOriginal = settings.OriginalAudioEnabled && sourceHasAudio;
        var includeVoice = settings.VietnameseVoiceEnabled;
        if (!includeOriginal && !includeVoice)
        {
            throw new LocalJobException(
                "AUDIO_TRACKS_DISABLED",
                "Hãy bật Âm gốc hoặc Giọng Việt trước khi xuất video.",
                retryable: false);
        }

        var originalVolume = Format(settings.OriginalAudioVolumePercent / 100d);
        var voiceVolume = Format(settings.VietnameseVoiceVolumePercent / 100d);
        if (includeOriginal && includeVoice)
        {
            return $"[0:a:0]aresample=48000,volume={originalVolume}[background];"
                + $"[1:a:0]aresample=48000,volume={voiceVolume},asplit=2[voice][sidechain];"
                + "[background][sidechain]sidechaincompress=threshold=0.03:ratio=8:attack=20:release=300[ducked];"
                + "[ducked][voice]amix=inputs=2:duration=first:normalize=0,alimiter=limit=0.95[mixed]";
        }

        return includeVoice
            ? $"[1:a:0]aresample=48000,volume={voiceVolume},alimiter=limit=0.95[mixed]"
            : $"[0:a:0]aresample=48000,volume={originalVolume},alimiter=limit=0.95[mixed]";
    }

    public static string BuildVietnameseSubtitleAss(
        IReadOnlyList<SubtitleCue> cues,
        SubtitleStyleSettings? configuredStyle,
        int videoWidth,
        int videoHeight)
    {
        if (cues.Count == 0)
        {
            throw new LocalJobException(
                "TRANSLATION_MISSING",
                "Chưa có phụ đề tiếng Việt hợp lệ để đè lên video.",
                retryable: false);
        }

        var ordered = cues
            .OrderBy(cue => cue.StartMilliseconds)
            .ThenBy(cue => cue.EndMilliseconds)
            .ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].StartMilliseconds < ordered[index - 1].EndMilliseconds)
            {
                throw new LocalJobException(
                    "SUBTITLE_TIMELINE_OVERLAP",
                    "Có hai câu phụ đề Việt trùng thời gian. Hãy chỉnh timeline trước khi xuất để tránh chữ bị chồng lớp.",
                    retryable: false);
            }
        }

        var style = SubtitleStyleRules.Normalize(configuredStyle);
        var width = videoWidth > 0 ? videoWidth : 1920;
        var height = videoHeight > 0 ? videoHeight : 1080;
        var fontSize = Math.Max(12, height * style.FontSizePercent / 100d);
        var outline = style.OutlineSize * height / 360d;
        var shadow = style.ShadowSize * height / 360d;
        var boxPadding = Math.Max(height / 360d, fontSize * 0.18);

        var alignment = GetAssAlignment(style);
        var margin = (int)Math.Round(width * (100d - style.MaxWidthPercent) / 200d);
        var positionX = width * style.PositionXPercent / 100d;
        var positionY = height * style.PositionYPercent / 100d;
        var backgroundAlpha = (byte)Math.Round(255d * (1d - style.BackgroundOpacity / 100d));
        var builder = new StringBuilder();
        builder.AppendLine("[Script Info]")
            .AppendLine("ScriptType: v4.00+")
            .Append("PlayResX: ").AppendLine(width.ToString(CultureInfo.InvariantCulture))
            .Append("PlayResY: ").AppendLine(height.ToString(CultureInfo.InvariantCulture))
            .AppendLine("WrapStyle: 1")
            .AppendLine("ScaledBorderAndShadow: yes")
            .AppendLine("YCbCr Matrix: TV.709")
            .AppendLine()
            .AppendLine("[V4+ Styles]")
            .AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding")
            .Append("Style: SubVid,")
            .Append(style.FontFamily).Append(',')
            .Append(Format(fontSize)).Append(',')
            .Append(ToAssColor(style.TextColor, 0)).Append(',')
            .Append(ToAssColor(style.TextColor, 0)).Append(',')
            .Append(ToAssColor(style.OutlineColor, 0)).Append(',')
            .Append("&H80000000,")
            .Append(style.Bold ? "-1" : "0")
            .Append(",0,0,0,100,100,0,0,1,")
            .Append(Format(outline)).Append(',')
            .Append(Format(shadow)).Append(',')
            .Append(alignment).Append(',')
            .Append(margin).Append(',')
            .Append(margin).AppendLine(",0,1");

        if (style.BackgroundMode == "box")
        {
            var backgroundColor = ToAssColor(style.BackgroundColor, backgroundAlpha);
            builder.Append("Style: SubVidBox,")
                .Append(style.FontFamily).Append(',')
                .Append(Format(fontSize)).Append(',')
                .Append(ToAssColor(style.TextColor, 255)).Append(',')
                .Append(ToAssColor(style.TextColor, 255)).Append(',')
                .Append(backgroundColor).Append(',')
                .Append(backgroundColor).Append(',')
                .Append(style.Bold ? "-1" : "0")
                .Append(",0,0,0,100,100,0,0,3,")
                .Append(Format(boxPadding)).Append(",0,")
                .Append(alignment).Append(',')
                .Append(margin).Append(',')
                .Append(margin).AppendLine(",0,1");
        }

        builder.AppendLine()
            .AppendLine("[Events]")
            .AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        var positionOverride = $"{{\\an{alignment}\\pos({Format(positionX)},{Format(positionY)})}}";
        foreach (var cue in ordered)
        {
            var wrappedText = EscapeAssText(NormalizeSubtitleText(cue.TranslatedText));
            if (style.BackgroundMode == "box")
            {
                builder.Append("Dialogue: 0,")
                    .Append(FormatAssTimestamp(cue.StartMilliseconds)).Append(',')
                    .Append(FormatAssTimestamp(cue.EndMilliseconds))
                    .Append(",SubVidBox,,0,0,0,,")
                    .Append(positionOverride)
                    .AppendLine(wrappedText);
            }

            builder.Append("Dialogue: 1,")
                .Append(FormatAssTimestamp(cue.StartMilliseconds)).Append(',')
                .Append(FormatAssTimestamp(cue.EndMilliseconds))
                .Append(",SubVid,,0,0,0,,")
                .Append(positionOverride)
                .AppendLine(wrappedText);
        }

        return builder.ToString();
    }

    private static int GetAssAlignment(SubtitleStyleSettings style)
    {
        var vertical = style.VerticalPosition == "custom"
            ? style.PositionYPercent < 34 ? "top" : style.PositionYPercent > 66 ? "bottom" : "middle"
            : style.VerticalPosition;
        var horizontalOffset = style.HorizontalAlignment switch
        {
            "left" => 1,
            "right" => 3,
            _ => 2,
        };
        return vertical switch
        {
            "top" => 6 + horizontalOffset,
            "middle" => 3 + horizontalOffset,
            _ => horizontalOffset,
        };
    }

    private static string ToAssColor(string hex, byte alpha)
    {
        var normalized = hex.TrimStart('#');
        var red = Convert.ToByte(normalized[..2], 16);
        var green = Convert.ToByte(normalized[2..4], 16);
        var blue = Convert.ToByte(normalized[4..6], 16);
        return $"&H{alpha:X2}{blue:X2}{green:X2}{red:X2}";
    }

    private static string FormatAssTimestamp(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        var centiseconds = time.Milliseconds / 10;
        return $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{centiseconds:00}";
    }

    private static string NormalizeSubtitleText(string text) =>
        string.Join(' ', text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string EscapeAssText(string text) =>
        text.Replace("\\", "＼", StringComparison.Ordinal)
            .Replace("{", "｛", StringComparison.Ordinal)
            .Replace("}", "｝", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\N", StringComparison.Ordinal);

    private static IReadOnlyList<SubtitleRemovalRegionSettings> GetEffectiveSubtitleRemovalRegions(
        ProjectSettings settings) =>
        settings.OriginalSubtitleRemovalRegions is { Count: > 0 }
            ? settings.OriginalSubtitleRemovalRegions
            :
            [
                new SubtitleRemovalRegionSettings
                {
                    Id = "legacy",
                    X = settings.OriginalSubtitleRegionX,
                    Y = settings.OriginalSubtitleRegionY,
                    Width = settings.OriginalSubtitleRegionWidth,
                    Height = settings.OriginalSubtitleRegionHeight,
                },
            ];

    private static void ValidateRemovalSettings(
        ProjectSettings settings,
        IReadOnlyList<SubtitleRemovalRegionSettings> regions)
    {
        var mode = settings.OriginalSubtitleRemovalMode.Trim().ToLowerInvariant();
        if (mode is not ("blur" or "cover")
            || regions.Count is < 1 or > DesktopWorkspaceCoordinator.MaxSubtitleRemovalRegions
            || regions.Any(region =>
                !double.IsFinite(region.X)
                || !double.IsFinite(region.Y)
                || !double.IsFinite(region.Width)
                || !double.IsFinite(region.Height)
                || region.X < 0
                || region.Y < 0
                || region.Width < 0.05
                || region.Height < 0.04
                || region.X + region.Width > 1.000001
                || region.Y + region.Height > 1.000001))
        {
            throw new LocalJobException(
                "SUBTITLE_REMOVAL_REGION_INVALID",
                "Vùng xóa phụ đề gốc không hợp lệ.",
                retryable: false);
        }
    }

    private static void ValidateAudioSettings(ProjectSettings settings)
    {
        if (!double.IsFinite(settings.OriginalAudioVolumePercent)
            || settings.OriginalAudioVolumePercent < 0
            || settings.OriginalAudioVolumePercent > 100
            || !double.IsFinite(settings.VietnameseVoiceVolumePercent)
            || settings.VietnameseVoiceVolumePercent < 0
            || settings.VietnameseVoiceVolumePercent > 100)
        {
            throw new LocalJobException(
                "AUDIO_SETTINGS_INVALID",
                "Âm lượng phải nằm trong khoảng từ 0 đến 100.",
                retryable: false);
        }
    }

    private static string EscapeFilterPath(string path) =>
        Path.GetFullPath(path)
            .Replace('\\', '/')
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

    private static string Format(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static void RejectSourceOverwrite(
        string destination,
        LocalMediaReference source,
        string effectiveSourcePath)
    {
        var originalPath = string.IsNullOrWhiteSpace(source.OriginalPath)
            ? null
            : Path.GetFullPath(source.OriginalPath);
        if (string.Equals(destination, effectiveSourcePath, StringComparison.OrdinalIgnoreCase)
            || (originalPath is not null
                && string.Equals(destination, originalPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new LocalJobException(
                "EXPORT_SOURCE_OVERWRITE_BLOCKED",
                "Không được ghi đè trực tiếp lên video gốc. Hãy chọn tên file khác.",
                retryable: false);
        }
    }

    private static async Task<bool> HasExpectedHashAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedHash)) return false;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHash));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
