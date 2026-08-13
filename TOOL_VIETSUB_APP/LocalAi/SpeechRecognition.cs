using System.Runtime.CompilerServices;
using Whisper.net;

namespace TOOL_VIETSUB_APP.LocalAi;

public sealed record SpeechRecognitionSegment(
    long StartMilliseconds,
    long EndMilliseconds,
    string Text,
    string Language,
    float Confidence);

public interface ILocalSpeechRecognizer
{
    IAsyncEnumerable<SpeechRecognitionSegment> RecognizeAsync(
        string wavePath,
        string? languageCode,
        CancellationToken cancellationToken);
}

public sealed class WhisperLocalSpeechRecognizer : ILocalSpeechRecognizer
{
    public const string ModelId = "whisper-base-multilingual";
    public static readonly string ModelRelativePath = Path.Combine("whisper", "ggml-base.bin");

    private readonly LocalModelManager _models;
    private readonly int _threads;

    public WhisperLocalSpeechRecognizer(LocalModelManager models, int? threads = null)
    {
        _models = models;
        _threads = Math.Clamp(threads ?? Environment.ProcessorCount / 2, 2, 8);
    }

    public async IAsyncEnumerable<SpeechRecognitionSegment> RecognizeAsync(
        string wavePath,
        string? languageCode,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var wave = new FileInfo(wavePath);
        if (!wave.Exists || wave.Length <= 44)
        {
            throw new LocalModelException(
                "SPEECH_AUDIO_INVALID",
                "Audio nhận dạng không tồn tại hoặc không hợp lệ.");
        }

        var modelPath = _models.RequireFile(ModelId, ModelRelativePath);
        await using var enumerator = RecognizeCoreAsync(
            modelPath,
            wave.FullName,
            LocalLanguageCodes.NormalizeSource(languageCode),
            _threads,
            cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            SpeechRecognitionSegment current;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                current = enumerator.Current;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (LocalModelException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new LocalModelException(
                    "SPEECH_RECOGNITION_FAILED",
                    "Không thể nhận dạng audio bằng Whisper local.",
                    exception);
            }

            yield return current;
        }
    }

    private static async IAsyncEnumerable<SpeechRecognitionSegment> RecognizeCoreAsync(
        string modelPath,
        string wavePath,
        string? languageCode,
        int threads,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions
        {
            UseGpu = false,
        });
        var builder = factory.CreateBuilder()
            .WithThreads(threads)
            .WithProbabilities();
        if (languageCode is "en" or "zh")
        {
            builder.WithLanguage(languageCode);
        }
        else
        {
            builder.WithLanguageDetection();
        }

        await using var processor = builder.Build();
        await using var stream = new FileStream(
            wavePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await foreach (var segment in processor.ProcessAsync(stream, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = segment.Text.Trim();
            var start = Math.Max(0, (long)segment.Start.TotalMilliseconds);
            var end = Math.Max(start + 1, (long)segment.End.TotalMilliseconds);
            if (text.Length == 0)
            {
                continue;
            }

            yield return new SpeechRecognitionSegment(
                start,
                end,
                text,
                string.IsNullOrWhiteSpace(segment.Language) ? "und" : segment.Language.Trim().ToLowerInvariant(),
                Math.Clamp(segment.Probability, 0, 1));
        }
    }
}
