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

        var exception = await Assert.ThrowsAsync<LocalJobException>(() =>
            executor.ExecuteAsync(job, _ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.Equal("TRANSLATION_OUTPUT_INVALID", exception.Code);
        Assert.Equal(originalTranslation, cue.TranslatedText);
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
        Assert.All(cues.Skip(1), cue =>
        {
            Assert.StartsWith("R: VI:", cue.TranslatedText);
            Assert.Equal("openai:gpt-test", cue.TranslationModelId);
            Assert.NotNull(cue.TranslationReviewedAtUtc);
        });
        Assert.Equal(4, provider.Requests.Count);
        Assert.Equal(2, provider.Requests.Count(request => request.Pass == TranslationPass.Translate));
        Assert.Contains(provider.Requests, request => request.Cues.Any(cue => !cue.IsTarget));
        Assert.All(provider.Requests.Where(request => request.Pass == TranslationPass.Review), request =>
            Assert.All(request.Cues.Where(cue => cue.IsTarget), cue =>
                Assert.False(string.IsNullOrWhiteSpace(cue.CandidateTranslation))));
        Assert.All(cues.Skip(1), cue =>
        {
            Assert.False(TranslationQualityValidator.LooksPathological(cue.OriginalText, cue.TranslatedText));
            Assert.False(string.IsNullOrWhiteSpace(cue.TranslationSourceFingerprint));
        });
        var secondJob = new LocalJob { Steps = [new LocalJobStep { Code = "TRANSLATE" }] };
        await executor.ExecuteAsync(secondJob, _ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(4, provider.Requests.Count);
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

        public Task SynthesizeAsync(
            IReadOnlyList<VoiceSynthesisRequest> items,
            CancellationToken cancellationToken)
        {
            SynthesizedItems += items.Count;
            foreach (var item in items)
            {
                WriteWave(item.OutputPath, 16_000, seconds: 1);
            }

            return Task.CompletedTask;
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
                        ? $"R: {cue.CandidateTranslation}"
                        : $"VI: {cue.OriginalText}",
                    0.95,
                    [])).ToArray();
            return Task.FromResult(new TranslationSceneResult(
                ProviderId,
                ModelId,
                ModelId,
                results));
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
