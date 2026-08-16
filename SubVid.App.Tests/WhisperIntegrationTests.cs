using SubVid.App.Core;
using SubVid.App.LocalAi;

namespace SubVid.App.Tests;

public sealed class WhisperIntegrationTests
{
    [Fact]
    public async Task Recognize_WithInstalledModel_ProducesTimedSpeech()
    {
        var modelRoot = Environment.GetEnvironmentVariable("SUBVID_TEST_WHISPER_MODEL_ROOT");
        var wavePath = Environment.GetEnvironmentVariable("SUBVID_TEST_SPEECH_WAV");
        if (string.IsNullOrWhiteSpace(modelRoot) || string.IsNullOrWhiteSpace(wavePath))
        {
            return;
        }

        var previous = Environment.GetEnvironmentVariable("SUBVID_MODEL_ROOT");
        Environment.SetEnvironmentVariable("SUBVID_MODEL_ROOT", modelRoot);
        try
        {
            var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "SUBVID_WHISPER_TEST"));
            using var models = new LocalModelManager(paths);
            var recognizer = new WhisperLocalSpeechRecognizer(models, threads: 4);
            var segments = new List<SpeechRecognitionSegment>();

            await foreach (var segment in recognizer.RecognizeAsync(wavePath, null, CancellationToken.None))
            {
                segments.Add(segment);
            }

            Assert.NotEmpty(segments);
            Assert.All(segments, segment =>
            {
                Assert.False(string.IsNullOrWhiteSpace(segment.Text));
                Assert.True(segment.EndMilliseconds > segment.StartMilliseconds);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUBVID_MODEL_ROOT", previous);
        }
    }
}
