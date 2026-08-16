using System.Text;
using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.Jobs;
using TOOL_VIETSUB_APP.LocalAi;
using TOOL_VIETSUB_APP.Translation;

namespace TOOL_VIETSUB_APP.Tests;

public sealed class LanguagePipelineJobTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "TOOL_VIETSUB_TESTS",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Translation_UsesBatchesAndDoesNotOverwriteReviewedText()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Translation test");
        project.SourceLanguageCode = "en";
        var cues = Enumerable.Range(1, 18).Select(index => new SubtitleCue
        {
            StartMilliseconds = index * 1000,
            EndMilliseconds = index * 1000 + 800,
            OriginalText = $"Sentence {index}",
        }).ToList();
        cues[0].TranslatedText = "Đã duyệt";
        cues[0].TranslationLocked = true;
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "en", Cues = cues });
        var translator = new FakeTranslator();
        var job = new LocalJob
        {
            Steps = [new LocalJobStep { Code = "TRANSLATE" }],
        };
        var executor = new TranslationJobExecutor(paths, workspace, project, translator);

        await executor.ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("Đã duyệt", cues[0].TranslatedText);
        Assert.All(cues.Skip(1), cue => Assert.StartsWith("VI:", cue.TranslatedText));
        Assert.Equal([12, 5], translator.BatchSizes);
        Assert.True(File.Exists(paths.GetProjectPath(project.ProjectId, job.Steps[0].OutputRelativePath!)));
    }

    [Fact]
    public async Task Translation_DoesNotSilentlyTreatUnknownSourceAsEnglish()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Unknown language");
        project.SubtitleTracks.Add(new SubtitleDocument
        {
            LanguageCode = "und",
            Cues =
            [
                new SubtitleCue
                {
                    StartMilliseconds = 0,
                    EndMilliseconds = 1000,
                    OriginalText = "未知语言",
                },
            ],
        });
        var job = new LocalJob
        {
            Steps = [new LocalJobStep { Code = "TRANSLATE" }],
        };
        var executor = new TranslationJobExecutor(paths, workspace, project, new FakeTranslator());

        var exception = await Assert.ThrowsAsync<LocalJobException>(() =>
            executor.ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("TRANSLATION_SOURCE_REQUIRED", exception.Code);
    }

    [Fact]
    public async Task Translation_InvalidBatchDoesNotOverwriteExistingText()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Invalid translation");
        project.SourceLanguageCode = "zh";
        var originalTranslation = "dưới dưới dưới dưới dưới dưới dưới dưới";
        var cue = new SubtitleCue
        {
            StartMilliseconds = 0,
            EndMilliseconds = 1000,
            OriginalText = "这是小米",
            TranslatedText = originalTranslation,
        };
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "zh", Cues = [cue] });
        var job = new LocalJob
        {
            Parameters = new Dictionary<string, string> { ["sourceLanguage"] = "zh" },
            Steps = [new LocalJobStep { Code = "TRANSLATE" }],
        };
        var executor = new TranslationJobExecutor(paths, workspace, project, new InvalidTranslator());

        await executor.ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(originalTranslation, cue.TranslatedText);
        Assert.Equal("REVIEW", cue.TranslationQualityStatus);
        Assert.Contains(cue.TranslationWarnings, warning =>
            warning.StartsWith("TRANSLATION_INVALID:", StringComparison.Ordinal));
        Assert.Equal(1, job.TranslationMetrics?.SkippedCues);
    }

    [Fact]
    public async Task Translation_RepairsOnlyUnsafeCloudCueBeforeSaving()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Cloud safety repair");
        project.SourceLanguageCode = "en";
        project.Settings.TranslationProvider = TranslationProviders.OpenAi;
        project.Settings.TranslationModelId = "gpt-test";
        project.Settings.TranslationReviewEnabled = false;
        project.Settings.TranslationSceneMaxCues = 3;
        project.Settings.TranslationContextCueCount = 0;
        var cues = new[]
        {
            new SubtitleCue { StartMilliseconds = 0, EndMilliseconds = 1500, OriginalText = "First line" },
            new SubtitleCue { StartMilliseconds = 1600, EndMilliseconds = 3100, OriginalText = "Broken line" },
            new SubtitleCue { StartMilliseconds = 3200, EndMilliseconds = 4700, OriginalText = "Last line" },
        };
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "en", Cues = cues.ToList() });
        var provider = new SafetyRepairProvider(repairSucceeds: true);
        var executor = new TranslationJobExecutor(paths, workspace, project, provider);
        var job = new LocalJob
        {
            Parameters = new Dictionary<string, string> { ["sourceLanguage"] = "en" },
            Steps = [new LocalJobStep { Code = "TRANSLATE" }],
        };

        await executor.ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(TranslationPass.Translate, provider.Requests[0].Pass);
        Assert.Equal(TranslationPass.Review, provider.Requests[1].Pass);
        Assert.Equal([cues[1].CueId], provider.Requests[1].TargetCueIds);
        Assert.All(cues, cue => Assert.False(
            TranslationQualityValidator.LooksPathological(cue.OriginalText, cue.TranslatedText)));
        Assert.Contains("AUTO_REPAIRED", cues[1].TranslationWarnings);
        Assert.Equal("REVIEW", cues[1].TranslationQualityStatus);
        Assert.Null(cues[1].TranslationReviewedAtUtc);
    }

    [Fact]
    public async Task Translation_PersistentUnsafeCloudOutputKeepsOldTextAndIsNotCached()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Persistent cloud failure");
        project.SourceLanguageCode = "en";
        project.Settings.TranslationProvider = TranslationProviders.OpenAi;
        project.Settings.TranslationModelId = "gpt-test";
        project.Settings.TranslationReviewEnabled = false;
        project.Settings.TranslationContextCueCount = 0;
        var cue = new SubtitleCue
        {
            StartMilliseconds = 0,
            EndMilliseconds = 1500,
            OriginalText = "Broken line",
            TranslatedText = "Bản dịch cũ an toàn",
        };
        var safeCue = new SubtitleCue
        {
            StartMilliseconds = 1600,
            EndMilliseconds = 3100,
            OriginalText = "Safe line",
        };
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "en", Cues = [cue, safeCue] });
        var provider = new SafetyRepairProvider(repairSucceeds: false);
        var executor = new TranslationJobExecutor(paths, workspace, project, provider);
        var job = new LocalJob
        {
            Parameters = new Dictionary<string, string> { ["sourceLanguage"] = "en" },
            Steps = [new LocalJobStep { Code = "TRANSLATE" }],
        };

        await executor.ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal("Bản dịch cũ an toàn", cue.TranslatedText);
        Assert.Equal("Bản dịch: Safe line", safeCue.TranslatedText);
        Assert.Equal("REVIEW", cue.TranslationQualityStatus);
        Assert.Equal(1, job.TranslationMetrics?.CompletedCues);
        Assert.Equal(1, job.TranslationMetrics?.SkippedCues);
        var cacheDirectory = paths.GetProjectPath(project.ProjectId, "cache", "translation");
        Assert.False(Directory.Exists(cacheDirectory)
            && Directory.EnumerateFiles(cacheDirectory, "*.json").Any());
    }

    [Fact]
    public async Task Translation_RestartBypassesCacheButKeepsManualCueAndResumesByFingerprint()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Restart translation");
        project.SourceLanguageCode = "en";
        project.Settings.TranslationProvider = TranslationProviders.OpenAi;
        project.Settings.TranslationModelId = "gpt-test";
        project.Settings.TranslationContextCueCount = 0;
        var manualCue = new SubtitleCue
        {
            StartMilliseconds = 0,
            EndMilliseconds = 1000,
            OriginalText = "Manual",
            TranslatedText = "Bản dịch thủ công",
            TranslationLocked = true,
        };
        var aiCue = new SubtitleCue
        {
            StartMilliseconds = 1100,
            EndMilliseconds = 2200,
            OriginalText = "Translate me",
        };
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "en", Cues = [manualCue, aiCue] });
        var provider = new CountingProvider();
        var executor = new TranslationJobExecutor(paths, workspace, project, provider);
        var firstJob = new LocalJob
        {
            Parameters = new Dictionary<string, string> { ["sourceLanguage"] = "en" },
            Steps = [new LocalJobStep { Code = "TRANSLATE" }],
        };

        await executor.ExecuteAsync(firstJob, _ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal("Lượt 1: Translate me", aiCue.TranslatedText);

        var restartJob = new LocalJob
        {
            Parameters = new Dictionary<string, string>
            {
                ["sourceLanguage"] = "en",
                [TranslationRunModes.ParameterName] = TranslationRunModes.Restart,
            },
            Steps = [new LocalJobStep { Code = "TRANSLATE" }],
        };
        await executor.ExecuteAsync(restartJob, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(2, provider.RequestCount);
        Assert.Equal("Lượt 2: Translate me", aiCue.TranslatedText);
        Assert.Equal("Bản dịch thủ công", manualCue.TranslatedText);
        Assert.True(bool.Parse(restartJob.Parameters[TranslationRunModes.RestartPreparedParameterName]));

        await executor.ExecuteAsync(restartJob, _ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(2, provider.RequestCount);
    }

    [Fact]
    public async Task Translation_UsesSceneContextAndCloudReviewWithoutChangingLockedCue()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Contextual translation");
        project.SourceLanguageCode = "en";
        project.Settings.TranslationProvider = TranslationProviders.OpenAi;
        project.Settings.TranslationModelId = "gpt-test";
        project.Settings.TranslationSceneMaxCues = 2;
        project.Settings.TranslationContextCueCount = 1;
        project.Settings.TranslationReviewEnabled = true;
        project.TranslationContext.Summary = "Two colleagues are discussing a release.";
        project.TranslationGlossary.Add(new TranslationGlossaryEntry
        {
            SourceText = "release",
            TargetText = "bản phát hành",
        });
        var cues = Enumerable.Range(1, 5).Select(index => new SubtitleCue
        {
            StartMilliseconds = index * 2500,
            EndMilliseconds = index * 2500 + 2000,
            OriginalText = index == 3 ? "The release is ready" : $"Sentence {index}",
        }).ToList();
        cues[0].TranslatedText = "Đã duyệt";
        cues[0].TranslationLocked = true;
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "en", Cues = cues });
        var provider = new ReviewingProvider();
        var executor = new TranslationJobExecutor(paths, workspace, project, provider);
        var job = new LocalJob { Steps = [new LocalJobStep { Code = "TRANSLATE" }] };

        await executor.ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("Đã duyệt", cues[0].TranslatedText);
        Assert.StartsWith("VI:", cues[1].TranslatedText);
        Assert.Contains("bản phát hành", cues[2].TranslatedText, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("VI:", cues[3].TranslatedText);
        Assert.StartsWith("VI:", cues[4].TranslatedText);
        Assert.All(cues.Skip(1), cue =>
        {
            Assert.Equal("openai:gpt-test", cue.TranslationModelId);
        });
        Assert.Null(cues[1].TranslationReviewedAtUtc);
        Assert.NotNull(cues[2].TranslationReviewedAtUtc);
        Assert.Null(cues[3].TranslationReviewedAtUtc);
        Assert.Null(cues[4].TranslationReviewedAtUtc);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal(2, provider.Requests.Count(request => request.Pass == TranslationPass.Translate));
        var reviewRequest = Assert.Single(
            provider.Requests,
            request => request.Pass == TranslationPass.Review);
        Assert.Equal([cues[2].CueId], reviewRequest.TargetCueIds);
        Assert.Contains(provider.Requests, request => request.Cues.Any(cue => !cue.IsTarget));
        Assert.All(provider.Requests.Where(request => request.Pass == TranslationPass.Review), request =>
            Assert.All(request.Cues.Where(cue => cue.IsTarget), cue =>
                Assert.False(string.IsNullOrWhiteSpace(cue.CandidateTranslation))));
        Assert.NotNull(job.TranslationMetrics);
        Assert.Equal(150, job.TranslationMetrics.InputTokens);
        Assert.Equal(30, job.TranslationMetrics.OutputTokens);
        Assert.Equal(15, job.TranslationMetrics.CachedInputTokens);
        Assert.Equal(3, job.TranslationMetrics.ApiRequests);
        Assert.Equal(1, job.TranslationMetrics.ReviewedCues);
        Assert.All(cues.Skip(1), cue =>
        {
            Assert.False(TranslationQualityValidator.LooksPathological(cue.OriginalText, cue.TranslatedText));
            Assert.False(string.IsNullOrWhiteSpace(cue.TranslationSourceFingerprint));
        });
        var secondJob = new LocalJob { Steps = [new LocalJobStep { Code = "TRANSLATE" }] };
        await executor.ExecuteAsync(secondJob, _ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(3, provider.Requests.Count);
    }

    [Fact]
    public async Task VoiceSynthesis_WritesAtomicCueFilesAndMetadata()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Voice test");
        var cues = new[]
        {
            new SubtitleCue { StartMilliseconds = 0, EndMilliseconds = 1000, OriginalText = "One", TranslatedText = "Một" },
            new SubtitleCue { StartMilliseconds = 1200, EndMilliseconds = 2400, OriginalText = "Two", TranslatedText = "Hai" },
        };
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "en", Cues = cues.ToList() });
        var job = new LocalJob
        {
            Steps = [new LocalJobStep { Code = "SYNTHESIZE_VOICE" }],
        };
        var synthesizer = new FakeSynthesizer();
        var executor = new VoiceSynthesisJobExecutor(paths, workspace, project, synthesizer);

        await executor.ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None);

        var voices = project.AudioTracks.Where(track => track.Role == "VOICE_CUE").ToArray();
        Assert.Equal(2, voices.Length);
        Assert.All(voices, voice =>
        {
            Assert.NotNull(voice.CueId);
            Assert.False(string.IsNullOrWhiteSpace(voice.ContentFingerprint));
            Assert.True(voice.Metadata.DurationSeconds > 0.9);
            Assert.Equal(16_000, voice.Metadata.AudioSampleRate);
            Assert.True(File.Exists(paths.GetProjectPath(project.ProjectId, voice.WorkspaceRelativePath!)));
        });
        Assert.Empty(Directory.EnumerateFiles(paths.GetProjectPath(project.ProjectId, "temp"), "*.partial.wav"));

        var cacheJob = new LocalJob
        {
            Steps = [new LocalJobStep { Code = "SYNTHESIZE_VOICE" }],
        };
        await executor.ExecuteAsync(cacheJob, _ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(2, synthesizer.SynthesizedItems);
    }

    [Fact]
    public async Task VoiceSynthesis_PersistsEachCompletedCueAndRetrySkipsIt()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Incremental FPT voice test");
        project.Settings.VoiceId = "fpt:banmai";
        var cues = new[]
        {
            new SubtitleCue { StartMilliseconds = 0, EndMilliseconds = 1000, OriginalText = "One", TranslatedText = "Một" },
            new SubtitleCue { StartMilliseconds = 1200, EndMilliseconds = 2400, OriginalText = "Two", TranslatedText = "Hai" },
        };
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "en", Cues = cues.ToList() });
        var failedJob = new LocalJob
        {
            Steps = [new LocalJobStep { Code = "SYNTHESIZE_VOICE" }],
        };
        var failingSynthesizer = new FailingIncrementalSynthesizer();
        var executor = new VoiceSynthesisJobExecutor(paths, workspace, project, failingSynthesizer);

        var exception = await Assert.ThrowsAsync<LocalJobException>(() =>
            executor.ExecuteAsync(failedJob, _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("FPT_NETWORK_ERROR", exception.Code);
        var firstTrack = Assert.Single(project.AudioTracks, track => track.Role == "VOICE_CUE");
        Assert.Equal(cues[0].CueId, firstTrack.CueId);
        Assert.True(File.Exists(paths.GetProjectPath(project.ProjectId, firstTrack.WorkspaceRelativePath!)));
        Assert.Equal(1, failedJob.VoiceMetrics?.CompletedCues);

        var retrySynthesizer = new FakeSynthesizer();
        var retryExecutor = new VoiceSynthesisJobExecutor(paths, workspace, project, retrySynthesizer);
        await retryExecutor.ExecuteAsync(
            new LocalJob { AttemptCount = 2, Steps = [new LocalJobStep { Code = "SYNTHESIZE_VOICE" }] },
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal(1, retrySynthesizer.SynthesizedItems);
        Assert.Equal(cues[1].CueId, Assert.Single(retrySynthesizer.Requests).CueId);
        Assert.Equal(2, project.AudioTracks.Count(track => track.Role == "VOICE_CUE"));
    }

    [Fact]
    public async Task VoiceSynthesis_ResolvesCueSpeakerAndDefaultVoices_AndInvalidatesOnlyChangedCue()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Multi voice test");
        project.Settings.VoiceId = "vieneu:minh-duc";
        project.Settings.SpeakerVoiceIds["speaker_2"] = "vieneu:truc-ly";
        var cues = new[]
        {
            new SubtitleCue { Speaker = "speaker_1", StartMilliseconds = 0, EndMilliseconds = 1000, OriginalText = "One", TranslatedText = "Một" },
            new SubtitleCue { Speaker = "speaker_2", StartMilliseconds = 1200, EndMilliseconds = 2200, OriginalText = "Two", TranslatedText = "Hai" },
            new SubtitleCue { Speaker = "speaker_2", VoiceId = LocalVoiceCatalog.DefaultVoiceId, StartMilliseconds = 2400, EndMilliseconds = 3400, OriginalText = "Three", TranslatedText = "Ba" },
        };
        project.SubtitleTracks.Add(new SubtitleDocument { LanguageCode = "en", Cues = cues.ToList() });
        var synthesizer = new FakeSynthesizer();
        var executor = new VoiceSynthesisJobExecutor(paths, workspace, project, synthesizer);

        await executor.ExecuteAsync(
            new LocalJob { Steps = [new LocalJobStep { Code = "SYNTHESIZE_VOICE" }] },
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal(
            ["vieneu:minh-duc", "vieneu:truc-ly", LocalVoiceCatalog.DefaultVoiceId],
            synthesizer.Requests.Select(request => request.VoiceId).ToArray());

        cues[1].VoiceId = "vieneu:ngoc-linh";
        await executor.ExecuteAsync(
            new LocalJob { Steps = [new LocalJobStep { Code = "SYNTHESIZE_VOICE" }] },
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal(4, synthesizer.SynthesizedItems);
        Assert.Equal(cues[1].CueId, synthesizer.Requests[^1].CueId);
        Assert.Equal("vieneu:ngoc-linh", synthesizer.Requests[^1].VoiceId);
    }

    [Fact]
    public async Task VoiceSynthesis_RejectsPathologicalTranslation()
    {
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Invalid voice input");
        project.SubtitleTracks.Add(new SubtitleDocument
        {
            LanguageCode = "zh",
            Cues =
            [
                new SubtitleCue
                {
                    StartMilliseconds = 0,
                    EndMilliseconds = 1000,
                    OriginalText = "这是小米",
                    TranslatedText = "So So So So So So So So So So So So",
                },
            ],
        });
        var executor = new VoiceSynthesisJobExecutor(
            paths,
            workspace,
            project,
            new FakeSynthesizer());
        var job = new LocalJob { Steps = [new LocalJobStep { Code = "SYNTHESIZE_VOICE" }] };

        var exception = await Assert.ThrowsAsync<LocalJobException>(() =>
            executor.ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("TRANSLATION_QUALITY_INVALID", exception.Code);
    }

    [Fact]
    public void WaveMetadata_RejectsInvalidFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "bad.wav");
        File.WriteAllText(path, "not a wave", Encoding.UTF8);

        var exception = Assert.Throws<LocalJobException>(() => WaveFileMetadata.Read(path));

        Assert.Equal("VOICE_WAVE_INVALID", exception.Code);
    }

    private sealed class FakeTranslator : ILocalTranslator
    {
        public List<int> BatchSizes { get; } = [];

        public Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts,
            string sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken)
        {
            BatchSizes.Add(texts.Count);
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(text => $"VI: {text}").ToArray());
        }
    }

    private sealed class FakeSynthesizer : ILocalVoiceSynthesizer
    {
        public int SynthesizedItems { get; private set; }

        public List<VoiceSynthesisRequest> Requests { get; } = [];

        public Task SynthesizeAsync(
            IReadOnlyList<VoiceSynthesisRequest> items,
            CancellationToken cancellationToken)
        {
            SynthesizedItems += items.Count;
            Requests.AddRange(items);
            foreach (var item in items)
            {
                WriteWave(item.OutputPath, 16_000, seconds: 1);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FailingIncrementalSynthesizer : IIncrementalVoiceSynthesizer
    {
        public Task SynthesizeAsync(
            IReadOnlyList<VoiceSynthesisRequest> items,
            CancellationToken cancellationToken) =>
            SynthesizeIncrementallyAsync(items, _ => ValueTask.CompletedTask, cancellationToken);

        public async Task SynthesizeIncrementallyAsync(
            IReadOnlyList<VoiceSynthesisRequest> items,
            Func<VoiceSynthesisRequest, ValueTask> onCompleted,
            CancellationToken cancellationToken)
        {
            WriteWave(items[0].OutputPath, 16_000, seconds: 1);
            await onCompleted(items[0]);
            throw new VoiceSynthesisException(
                "FPT_NETWORK_ERROR",
                "Mất kết nối FPT.AI sau cue đầu tiên.");
        }
    }

    private sealed class InvalidTranslator : ILocalTranslator
    {
        public Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts,
            string sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(
                texts.Select(_ => "pha pha pha pha pha pha pha pha pha pha pha pha").ToArray());
    }

    private sealed class ReviewingProvider : ITranslationProvider
    {
        public List<TranslationSceneRequest> Requests { get; } = [];

        public string ProviderId => TranslationProviders.OpenAi;

        public string ModelId => "gpt-test";

        public bool SupportsContextualReview => true;

        public Task<TranslationSceneResult> TranslateAsync(
            TranslationSceneRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var results = request.Cues.Where(cue => cue.IsTarget).Select(cue =>
                new TranslationItemResult(
                    cue.CueId,
                    request.Pass == TranslationPass.Review
                        ? cue.OriginalText.Contains("release", StringComparison.OrdinalIgnoreCase)
                            ? "Bản phát hành đã sẵn sàng"
                            : $"R: {cue.CandidateTranslation}"
                        : $"VI: {cue.OriginalText}",
                    0.95,
                    [])).ToArray();
            return Task.FromResult(new TranslationSceneResult(
                ProviderId,
                ModelId,
                ModelId,
                results,
                new TranslationUsage(50, 10, 5, 1, 0)));
        }
    }

    private sealed class SafetyRepairProvider(bool repairSucceeds) : ITranslationProvider
    {
        public List<TranslationSceneRequest> Requests { get; } = [];

        public string ProviderId => TranslationProviders.OpenAi;

        public string ModelId => "gpt-test";

        public bool SupportsContextualReview => true;

        public Task<TranslationSceneResult> TranslateAsync(
            TranslationSceneRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var items = request.Cues.Where(cue => cue.IsTarget).Select(cue =>
                new TranslationItemResult(
                    cue.CueId,
                    cue.OriginalText == "Broken line"
                        && (request.Pass == TranslationPass.Translate || !repairSucceeds)
                            ? "lặp lặp lặp lặp lặp lặp lặp lặp"
                            : $"Bản dịch: {cue.OriginalText}",
                    0.94,
                    [])).ToArray();
            return Task.FromResult(new TranslationSceneResult(
                ProviderId,
                ModelId,
                ModelId,
                items));
        }
    }

    private sealed class CountingProvider : ITranslationProvider
    {
        public int RequestCount { get; private set; }

        public string ProviderId => TranslationProviders.OpenAi;

        public string ModelId => "gpt-test";

        public bool SupportsContextualReview => false;

        public Task<TranslationSceneResult> TranslateAsync(
            TranslationSceneRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new TranslationSceneResult(
                ProviderId,
                ModelId,
                ModelId,
                request.Cues.Where(cue => cue.IsTarget).Select(cue => new TranslationItemResult(
                    cue.CueId,
                    $"Lượt {RequestCount}: {cue.OriginalText}",
                    0.95,
                    [])).ToArray()));
        }
    }

    private static void WriteWave(string path, int sampleRate, int seconds)
    {
        var samples = sampleRate * seconds;
        var dataSize = samples * sizeof(short);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
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
        writer.Write(new byte[dataSize]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
