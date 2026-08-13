using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.Jobs;
using TOOL_VIETSUB_APP.LocalAi;

namespace TOOL_VIETSUB_APP.Tests;

public sealed class LocalLanguageServiceIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "TOOL_VIETSUB_LANGUAGE_TEST",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Argos_InstalledWorker_TranslatesEnglishToVietnamese()
    {
        if (!RuntimeConfigured()) return;
        var paths = new AppPaths(_root);
        using var models = new LocalModelManager(paths);
        var translator = new ArgosLocalTranslator(paths, models);

        var translated = await translator.TranslateAsync(
            ["Hello world."],
            "en",
            "vi",
            CancellationToken.None);

        var text = Assert.Single(translated);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.NotEqual("Hello world.", text);
    }

    [Fact]
    public async Task OpusMt_InstalledWorker_TranslatesChineseToVietnameseWithoutRepetition()
    {
        if (!RuntimeConfigured()) return;
        var paths = new AppPaths(_root);
        using var models = new LocalModelManager(paths);
        var translator = new OpusMtChineseVietnameseTranslator(paths, models);

        var translated = await translator.TranslateAsync(
            ["\u9996\u5148\uff0c\u5b83\u89c2\u5bdf\u6574\u4e2a\u7eff\u690d\u7684\u7ed3\u6784\u3002"],
            "zh",
            "vi",
            CancellationToken.None);

        var text = Assert.Single(translated);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.False(TranslationQualityValidator.LooksPathological(
            "\u9996\u5148\uff0c\u5b83\u89c2\u5bdf\u6574\u4e2a\u7eff\u690d\u7684\u7ed3\u6784\u3002",
            text));
    }

    [Fact]
    public async Task Piper_InstalledWorker_CreatesValidVietnameseWave()
    {
        if (!RuntimeConfigured()) return;
        Directory.CreateDirectory(_root);
        var paths = new AppPaths(_root);
        using var models = new LocalModelManager(paths);
        var synthesizer = new PiperLocalVoiceSynthesizer(paths, models);
        var output = Path.Combine(_root, "piper-test.wav");

        await synthesizer.SynthesizeAsync(
            [new VoiceSynthesisRequest(Guid.NewGuid(), "Xin chào, đây là giọng đọc tiếng Việt.", output)],
            CancellationToken.None);

        var metadata = WaveFileMetadata.Read(output);
        Assert.True(metadata.DurationSeconds > 0.5);
        Assert.True(new FileInfo(output).Length > 44);
    }

    private static bool RuntimeConfigured() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TOOL_VIETSUB_PYTHON_PATH"))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TOOL_VIETSUB_MODEL_ROOT"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
