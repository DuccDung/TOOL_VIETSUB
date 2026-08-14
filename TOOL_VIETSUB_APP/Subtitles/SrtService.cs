using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Subtitles;

public sealed class SrtException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed partial class SrtService
{
    private const int MaximumCueCount = 20_000;
    private const int MaximumTextLength = 10_000;
    private readonly AppPaths _paths;
    private readonly ProjectWorkspaceService _workspace;

    public SrtService(AppPaths paths, ProjectWorkspaceService workspace)
    {
        _paths = paths;
        _workspace = workspace;
    }

    public async Task<SubtitleDocument> ImportAsync(
        ProjectManifest project,
        string sourcePath,
        string languageCode,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(sourcePath);
        if (!file.Exists)
        {
            throw new SrtException("SRT_FILE_NOT_FOUND", "Không tìm thấy tệp phụ đề.");
        }

        if (!string.Equals(file.Extension, ".srt", StringComparison.OrdinalIgnoreCase)
            || file.Length <= 0
            || file.Length > 10 * 1024 * 1024)
        {
            throw new SrtException("SRT_FILE_INVALID", "Tệp SRT trống, quá lớn hoặc không đúng định dạng.");
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(file.FullName, new UTF8Encoding(false, true), cancellationToken);
        }
        catch (DecoderFallbackException)
        {
            throw new SrtException("SRT_ENCODING_UNSUPPORTED", "SRT phải sử dụng mã hóa UTF-8.");
        }

        var cues = Parse(text);
        var track = new SubtitleDocument
        {
            LanguageCode = NormalizeLanguage(languageCode),
            Source = "IMPORTED_SRT",
            Cues = cues,
        };
        var relativePath = Path.Combine("subtitles", $"imported-{track.TrackId:N}.srt");
        var destination = _paths.GetProjectPath(project.ProjectId, relativePath);
        var temporary = destination + ".partial";
        try
        {
            await File.WriteAllTextAsync(temporary, Serialize(cues), new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        project.SubtitleTracks.Add(track);
        await _workspace.SaveAsync(project, cancellationToken);
        return track;
    }

    public async Task UpdateCueAsync(
        ProjectManifest project,
        Guid cueId,
        string originalText,
        string translatedText,
        CancellationToken cancellationToken)
    {
        var cue = project.SubtitleTracks
            .SelectMany(track => track.Cues)
            .SingleOrDefault(item => item.CueId == cueId)
            ?? throw new SrtException("SUBTITLE_CUE_NOT_FOUND", "Không tìm thấy phân đoạn phụ đề.");
        var original = NormalizeText(originalText);
        var translated = NormalizeText(translatedText, allowEmpty: true);
        var voiceChanged = !string.Equals(cue.TranslatedText, translated, StringComparison.Ordinal);
        cue.OriginalText = original;
        cue.TranslatedText = translated;
        cue.TranslationModelId = string.IsNullOrWhiteSpace(translated) ? null : "manual";
        cue.TranslationModelVersion = null;
        cue.TranslationSourceFingerprint = null;
        cue.TranslationQualityStatus = string.IsNullOrWhiteSpace(translated) ? null : "VALID";
        cue.TranslationConfidence = null;
        cue.TranslationWarnings = [];
        cue.TranslationReviewedAtUtc = string.IsNullOrWhiteSpace(translated) ? null : DateTime.UtcNow;
        cue.OriginalLocked = true;
        cue.TranslationLocked = !string.IsNullOrWhiteSpace(translated);
        if (!string.IsNullOrWhiteSpace(translated))
        {
            RememberManualTranslation(project, original, translated);
        }
        if (voiceChanged)
        {
            InvalidateVoice(project, cue.CueId);
        }

        await _workspace.SaveAsync(project, cancellationToken);
    }

    private static void RememberManualTranslation(
        ProjectManifest project,
        string sourceText,
        string translatedText)
    {
        var sourceLanguage = TOOL_VIETSUB_APP.LocalAi.LocalLanguageCodes.ResolveProjectSource(project) ?? "und";
        var targetLanguage = string.IsNullOrWhiteSpace(project.TargetLanguageCode)
            ? "vi"
            : project.TargetLanguageCode.Trim().ToLowerInvariant();
        var existing = project.TranslationMemory.FirstOrDefault(entry =>
            string.Equals(entry.SourceLanguageCode, sourceLanguage, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.TargetLanguageCode, targetLanguage, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.SourceText, sourceText, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            project.TranslationMemory.Add(new TranslationMemoryEntry
            {
                SourceLanguageCode = sourceLanguage,
                TargetLanguageCode = targetLanguage,
                SourceText = sourceText,
                TranslatedText = translatedText,
                UpdatedAtUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.TranslatedText = translatedText;
            existing.UpdatedAtUtc = DateTime.UtcNow;
        }

        if (project.TranslationMemory.Count > 500)
        {
            var remove = project.TranslationMemory
                .OrderBy(entry => entry.UpdatedAtUtc)
                .Take(project.TranslationMemory.Count - 500)
                .ToHashSet();
            project.TranslationMemory.RemoveAll(remove.Contains);
        }
    }

    public async Task SplitCueAsync(
        ProjectManifest project,
        Guid cueId,
        long positionMilliseconds,
        CancellationToken cancellationToken)
    {
        var (track, cue, index) = FindCue(project, cueId);
        if (positionMilliseconds <= cue.StartMilliseconds + 100
            || positionMilliseconds >= cue.EndMilliseconds - 100)
        {
            throw new SrtException(
                "SUBTITLE_SPLIT_POSITION_INVALID",
                "Playhead phải nằm trong câu và cách mỗi mép ít nhất 100 ms.");
        }

        var (originalLeft, originalRight) = SplitText(cue.OriginalText);
        var (translatedLeft, translatedRight) = SplitText(cue.TranslatedText);
        var right = new SubtitleCue
        {
            StartMilliseconds = positionMilliseconds,
            EndMilliseconds = cue.EndMilliseconds,
            Speaker = cue.Speaker,
            OriginalText = originalRight,
            TranslatedText = translatedRight,
            OriginalLocked = true,
            TranslationLocked = !string.IsNullOrWhiteSpace(translatedRight),
        };
        cue.EndMilliseconds = positionMilliseconds;
        cue.OriginalText = originalLeft;
        cue.TranslatedText = translatedLeft;
        cue.OriginalLocked = true;
        cue.TranslationLocked = !string.IsNullOrWhiteSpace(translatedLeft);
        track.Cues.Insert(index + 1, right);
        InvalidateVoice(project, cue.CueId);
        await _workspace.SaveAsync(project, cancellationToken);
    }

    public async Task AlignCueStartAsync(
        ProjectManifest project,
        Guid cueId,
        long positionMilliseconds,
        CancellationToken cancellationToken)
    {
        var (_, cue, _) = FindCue(project, cueId);
        var duration = project.SourceVideo?.Metadata.DurationSeconds * 1000d;
        if (positionMilliseconds < 0
            || positionMilliseconds >= cue.EndMilliseconds - 100
            || (duration.HasValue && positionMilliseconds >= duration.Value))
        {
            throw new SrtException(
                "SUBTITLE_ALIGN_POSITION_INVALID",
                "Vị trí căn cue không hợp lệ hoặc làm cue ngắn hơn 100 ms.");
        }

        cue.StartMilliseconds = positionMilliseconds;
        await _workspace.SaveAsync(project, cancellationToken);
    }

    public async Task<Guid> DuplicateCueAsync(
        ProjectManifest project,
        Guid cueId,
        CancellationToken cancellationToken)
    {
        var (track, cue, index) = FindCue(project, cueId);
        var duration = cue.EndMilliseconds - cue.StartMilliseconds;
        var start = cue.EndMilliseconds;
        var end = start + duration;
        var mediaDuration = project.SourceVideo?.Metadata.DurationSeconds * 1000d;
        if (mediaDuration.HasValue && end > mediaDuration.Value)
        {
            end = (long)Math.Floor(mediaDuration.Value);
        }

        if (end <= start + 100)
        {
            throw new SrtException(
                "SUBTITLE_DUPLICATE_OUTSIDE_MEDIA",
                "Không còn đủ thời lượng video để nhân bản cue này.");
        }

        var copy = new SubtitleCue
        {
            StartMilliseconds = start,
            EndMilliseconds = end,
            Speaker = cue.Speaker,
            OriginalText = cue.OriginalText,
            TranslatedText = cue.TranslatedText,
            OriginalLocked = true,
            TranslationLocked = !string.IsNullOrWhiteSpace(cue.TranslatedText),
        };
        track.Cues.Insert(index + 1, copy);
        await _workspace.SaveAsync(project, cancellationToken);
        return copy.CueId;
    }

    public async Task DeleteCueAsync(
        ProjectManifest project,
        Guid cueId,
        CancellationToken cancellationToken)
    {
        var (track, _, index) = FindCue(project, cueId);
        track.Cues.RemoveAt(index);
        InvalidateVoice(project, cueId);
        await _workspace.SaveAsync(project, cancellationToken);
    }

    public async Task ExportAsync(
        ProjectManifest project,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var track = project.SubtitleTracks.LastOrDefault()
            ?? throw new SrtException("SUBTITLE_TRACK_MISSING", "Dự án chưa có phụ đề để xuất.");
        var fullPath = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".srt", StringComparison.OrdinalIgnoreCase))
        {
            fullPath += ".srt";
        }

        var temporary = fullPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                Serialize(track.Cues, preferTranslation: true),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                cancellationToken);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static List<SubtitleCue> Parse(string srt)
    {
        if (string.IsNullOrWhiteSpace(srt))
        {
            throw new SrtException("SRT_EMPTY", "Tệp SRT không có nội dung.");
        }

        var normalized = srt.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var blocks = BlankLineRegex().Split(normalized);
        if (blocks.Length > MaximumCueCount)
        {
            throw new SrtException("SRT_TOO_MANY_CUES", "SRT có quá nhiều phân đoạn.");
        }

        var cues = new List<SubtitleCue>(blocks.Length);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var timelineIndex = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (timelineIndex < 0 || timelineIndex >= lines.Length - 1)
            {
                throw new SrtException("SRT_TIMELINE_INVALID", "SRT có phân đoạn thiếu timestamp hoặc nội dung.");
            }

            var timeline = TimelineRegex().Match(lines[timelineIndex].Trim());
            if (!timeline.Success)
            {
                throw new SrtException("SRT_TIMELINE_INVALID", "Timestamp SRT không hợp lệ.");
            }

            var start = ParseTimestamp(timeline.Groups[1].Value);
            var end = ParseTimestamp(timeline.Groups[2].Value);
            if (end <= start)
            {
                throw new SrtException("SRT_TIMELINE_INVALID", "Thời điểm kết thúc phải sau thời điểm bắt đầu.");
            }

            var text = NormalizeText(string.Join('\n', lines.Skip(timelineIndex + 1)));
            cues.Add(new SubtitleCue
            {
                StartMilliseconds = start,
                EndMilliseconds = end,
                OriginalText = text,
            });
        }

        return cues;
    }

    private static (SubtitleDocument Track, SubtitleCue Cue, int Index) FindCue(
        ProjectManifest project,
        Guid cueId)
    {
        foreach (var track in project.SubtitleTracks)
        {
            var index = track.Cues.FindIndex(item => item.CueId == cueId);
            if (index >= 0) return (track, track.Cues[index], index);
        }

        throw new SrtException("SUBTITLE_CUE_NOT_FOUND", "Không tìm thấy phân đoạn phụ đề.");
    }

    private static (string Left, string Right) SplitText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (string.Empty, string.Empty);
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return (text.Trim(), text.Trim());
        var split = (words.Length + 1) / 2;
        return (string.Join(' ', words[..split]), string.Join(' ', words[split..]));
    }

    private static void InvalidateVoice(ProjectManifest project, Guid cueId) =>
        project.AudioTracks.RemoveAll(item =>
            item.Role == "VOICE_TIMELINE"
            || (item.Role == "VOICE_CUE" && item.CueId == cueId));

    public static string Serialize(
        IReadOnlyList<SubtitleCue> cues,
        bool preferTranslation = false)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            if (cue.StartMilliseconds < 0 || cue.EndMilliseconds <= cue.StartMilliseconds)
            {
                throw new SrtException("SRT_TIMELINE_INVALID", "Phân đoạn có timestamp không hợp lệ.");
            }

            var text = preferTranslation && !string.IsNullOrWhiteSpace(cue.TranslatedText)
                ? cue.TranslatedText
                : cue.OriginalText;
            builder.Append(index + 1).AppendLine();
            builder.Append(FormatTimestamp(cue.StartMilliseconds))
                .Append(" --> ")
                .Append(FormatTimestamp(cue.EndMilliseconds))
                .AppendLine();
            builder.AppendLine(text.Trim());
            if (index < cues.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static long ParseTimestamp(string value)
    {
        var parts = value.Split([':', ',', '.']);
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            || minutes > 59 || seconds > 59 || milliseconds > 999)
        {
            throw new SrtException("SRT_TIMELINE_INVALID", "Timestamp SRT không hợp lệ.");
        }

        return ((hours * 60L + minutes) * 60L + seconds) * 1000L + milliseconds;
    }

    private static string FormatTimestamp(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";
    }

    private static string NormalizeText(string value, bool allowEmpty = false)
    {
        var normalized = (value ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        if ((!allowEmpty && normalized.Length == 0) || normalized.Length > MaximumTextLength)
        {
            throw new SrtException("SUBTITLE_TEXT_INVALID", "Nội dung phụ đề trống hoặc vượt giới hạn.");
        }

        return normalized;
    }

    private static string NormalizeLanguage(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Length is >= 2 and <= 20 ? normalized : "und";
    }

    [GeneratedRegex(@"\n\s*\n+", RegexOptions.Compiled)]
    private static partial Regex BlankLineRegex();

    [GeneratedRegex(@"^(\d{1,3}:\d{2}:\d{2}[,.]\d{3})\s*-->\s*(\d{1,3}:\d{2}:\d{2}[,.]\d{3})(?:\s+.*)?$", RegexOptions.Compiled)]
    private static partial Regex TimelineRegex();
}
