using System.Security.Cryptography;
using System.Text;
using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.LocalAi;
using TOOL_VIETSUB_APP.Subtitles;

namespace TOOL_VIETSUB_APP.Jobs;

public sealed class TranslationJobExecutor : ILocalJobExecutor
{
    private readonly AppPaths _paths;
    private readonly ProjectWorkspaceService _workspace;
    private readonly ProjectManifest _project;
    private readonly ILocalTranslator _translator;

    public TranslationJobExecutor(
        AppPaths paths,
        ProjectWorkspaceService workspace,
        ProjectManifest project,
        ILocalTranslator translator)
    {
        _paths = paths;
        _workspace = workspace;
        _project = project;
        _translator = translator;
    }

    public async Task ExecuteAsync(
        LocalJob job,
        Func<JobProgressUpdate, ValueTask> reportProgress,
        CancellationToken cancellationToken)
    {
        var track = _project.SubtitleTracks.LastOrDefault(item => item.Cues.Count > 0)
            ?? throw new LocalJobException(
                "SUBTITLE_TRACK_MISSING",
                "Chưa có transcript để dịch.",
                retryable: false);
        var pending = track.Cues
            .Where(cue => !cue.TranslationLocked
                && (string.IsNullOrWhiteSpace(cue.TranslatedText)
                    || TranslationQualityValidator.LooksPathological(cue.OriginalText, cue.TranslatedText)))
            .ToArray();
        if (pending.Length == 0)
        {
            await reportProgress(new JobProgressUpdate("TRANSLATE", 100, 100, "Không còn phân đoạn cần dịch."));
            return;
        }

        var sourceLanguage = job.Parameters.GetValueOrDefault("sourceLanguage")
            ?? LocalLanguageCodes.ResolveProjectSource(_project)
            ?? throw new LocalJobException(
                "TRANSLATION_SOURCE_REQUIRED",
                "Hãy chọn tiếng Trung hoặc tiếng Anh trước khi dịch.",
                retryable: false);
        sourceLanguage = LocalLanguageCodes.NormalizeSource(sourceLanguage)
            ?? throw new LocalJobException(
                "TRANSLATION_SOURCE_REQUIRED",
                "Không xác định được ngôn ngữ nguồn để dịch.",
                retryable: false);
        var targetLanguage = job.Parameters.GetValueOrDefault("targetLanguage")
            ?? _project.TargetLanguageCode;
        var modelId = job.Parameters.GetValueOrDefault("modelId") ?? _project.Settings.TranslationModelId;
        var modelVersion = job.Parameters.GetValueOrDefault("modelVersion") ?? modelId;
        const int batchSize = 16;
        var completed = 0;
        for (var offset = 0; offset < pending.Length; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = pending.Skip(offset).Take(batchSize).ToArray();
            var translated = await _translator.TranslateAsync(
                batch.Select(cue => cue.OriginalText).ToArray(),
                sourceLanguage,
                targetLanguage,
                cancellationToken);
            if (translated.Count != batch.Length)
            {
                throw new LocalJobException(
                    "TRANSLATION_RESULT_INVALID",
                    "Model trả về số lượng bản dịch không hợp lệ.",
                    retryable: true);
            }

            var prepared = translated.Select((text, index) =>
            {
                var normalized = text.Trim();
                var quality = TranslationQualityValidator.ValidateText(batch[index].OriginalText, normalized);
                if (!quality.IsValid)
                {
                    throw new LocalJobException(
                        "TRANSLATION_OUTPUT_INVALID",
                        $"Bản dịch phân đoạn {batch[index].CueId} bị từ chối ({quality.Code}). Dữ liệu cũ vẫn được giữ nguyên.",
                        retryable: true);
                }

                return normalized;
            }).ToArray();

            for (var index = 0; index < batch.Length; index++)
            {
                var cue = batch[index];
                var changed = !string.Equals(cue.TranslatedText, prepared[index], StringComparison.Ordinal);
                cue.TranslatedText = prepared[index];
                cue.TranslationModelId = modelId;
                cue.TranslationModelVersion = modelVersion;
                cue.TranslationSourceFingerprint = BuildSourceFingerprint(cue.OriginalText);
                cue.TranslationQualityStatus = "VALID";
                if (changed)
                {
                    _project.AudioTracks.RemoveAll(item =>
                        item.Role == "VOICE_TIMELINE"
                        || (item.Role == "VOICE_CUE" && item.CueId == cue.CueId));
                }
            }

            completed += batch.Length;
            await _workspace.SaveAsync(_project, cancellationToken);
            var percent = completed * 100d / pending.Length;
            await reportProgress(new JobProgressUpdate(
                "TRANSLATE",
                percent,
                percent,
                $"Đã dịch và lưu {completed}/{pending.Length} phân đoạn."));
        }

        var relativeOutput = Path.Combine("subtitles", $"translated-{track.TrackId:N}.srt");
        var outputPath = _paths.GetProjectPath(_project.ProjectId, relativeOutput);
        var partialPath = outputPath + ".partial";
        try
        {
            await File.WriteAllTextAsync(
                partialPath,
                SrtService.Serialize(track.Cues, preferTranslation: true),
                new UTF8Encoding(false),
                cancellationToken);
            File.Move(partialPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }

        job.Steps.Single(item => item.Code == "TRANSLATE").OutputRelativePath = relativeOutput;
        await _workspace.SaveAsync(_project, cancellationToken);
    }

    private static string BuildSourceFingerprint(string source)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
