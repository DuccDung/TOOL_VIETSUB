using SubVid.App.Core;
using SubVid.App.LocalAi;
using SubVid.App.Translation;

namespace SubVid.App.Tests;

public sealed class LocalTranslationProviderAdapterTests
{
    [Fact]
    public async Task TranslationMemory_MatchesNormalizedSourceWithoutCallingModel()
    {
        var translator = new RecordingTranslator();
        var provider = new LocalTranslationProviderAdapter(translator, "local-test");
        var request = CreateRequest(
            new TranslationCueInput(Guid.NewGuid(), 0, 1000, "speaker", "  Hello   world ", true, 40),
            memory: new TranslationMemoryEntry
            {
                SourceLanguageCode = "en",
                TargetLanguageCode = "vi",
                SourceText = "Hello world",
                TranslatedText = "Xin chào thế giới",
            });

        var result = await provider.TranslateAsync(request, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Xin chào thế giới", item.TranslatedText);
        Assert.Equal(0.98, item.Confidence);
        Assert.Empty(translator.Batches);
    }

    [Fact]
    public async Task LockedContextCue_IsReusedBeforeModelCall()
    {
        var translator = new RecordingTranslator();
        var provider = new LocalTranslationProviderAdapter(translator, "local-test");
        var request = CreateRequest(
            new TranslationCueInput(Guid.NewGuid(), 1000, 2000, "speaker", "Yes", true, 40),
            new TranslationCueInput(
                Guid.NewGuid(),
                0,
                1000,
                "speaker",
                "Yes",
                false,
                40,
                "Vâng"));

        var result = await provider.TranslateAsync(request, CancellationToken.None);

        Assert.Equal("Vâng", Assert.Single(result.Items).TranslatedText);
        Assert.Empty(translator.Batches);
    }

    [Fact]
    public async Task GlossaryTerm_IsAppliedToModelOutput()
    {
        var translator = new RecordingTranslator(_ => ["release is ready"]);
        var provider = new LocalTranslationProviderAdapter(translator, "local-test");
        var request = CreateRequest(
            new TranslationCueInput(Guid.NewGuid(), 0, 1000, "speaker", "The release is ready", true, 40),
            glossary: new TranslationGlossaryEntry
            {
                SourceText = "release",
                TargetText = "bản phát hành",
            });

        var result = await provider.TranslateAsync(request, CancellationToken.None);

        Assert.Equal("bản phát hành is ready", Assert.Single(result.Items).TranslatedText);
        Assert.Equal(["The release is ready"], translator.Batches.Single());
    }

    [Fact]
    public async Task RepeatedSourceText_IsTranslatedOncePerAdapterSession()
    {
        var translator = new RecordingTranslator(texts => texts.Select(text => $"VI: {text}").ToArray());
        var provider = new LocalTranslationProviderAdapter(translator, "local-test");
        var request = CreateRequest(
            new TranslationCueInput(Guid.NewGuid(), 0, 1000, "speaker", "Repeat", true, 40),
            new TranslationCueInput(Guid.NewGuid(), 1000, 2000, "speaker", "Repeat", true, 40));

        var result = await provider.TranslateAsync(request, CancellationToken.None);

        Assert.Collection(result.Items, _ => { }, _ => { });
        Assert.All(result.Items, item => Assert.Equal("VI: Repeat", item.TranslatedText));
        Assert.Single(translator.Batches);
        Assert.Equal(["Repeat"], translator.Batches.Single());
    }

    private static TranslationSceneRequest CreateRequest(
        params TranslationCueInput[] cues)
        => CreateRequest(cues, [], []);

    private static TranslationSceneRequest CreateRequest(
        TranslationCueInput cue,
        TranslationMemoryEntry memory)
        => CreateRequest([cue], [], [memory]);

    private static TranslationSceneRequest CreateRequest(
        TranslationCueInput cue,
        TranslationGlossaryEntry glossary)
        => CreateRequest([cue], [glossary], []);

    private static TranslationSceneRequest CreateRequest(
        IReadOnlyList<TranslationCueInput> cues,
        IReadOnlyList<TranslationGlossaryEntry> glossary,
        IReadOnlyList<TranslationMemoryEntry> memory)
        => new(
            "test",
            "en",
            "vi",
            string.Empty,
            string.Empty,
            string.Empty,
            glossary,
            memory,
            cues,
            TranslationPass.Translate);

    private sealed class RecordingTranslator(
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? translate = null)
        : ILocalTranslator
    {
        private readonly Func<IReadOnlyList<string>, IReadOnlyList<string>> _translate =
            translate ?? (texts => texts.Select(text => $"VI: {text}").ToArray());

        public List<IReadOnlyList<string>> Batches { get; } = [];

        public Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts,
            string sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken)
        {
            Batches.Add(texts.ToArray());
            return Task.FromResult(_translate(texts));
        }
    }
}
