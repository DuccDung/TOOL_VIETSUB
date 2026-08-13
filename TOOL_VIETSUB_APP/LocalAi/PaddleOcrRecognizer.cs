using System.Text.RegularExpressions;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;

namespace TOOL_VIETSUB_APP.LocalAi;

public sealed record OcrTextLine(string Text, float Confidence);

public interface ILocalOcrRecognizer : IDisposable
{
    Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(
        string imagePath,
        CancellationToken cancellationToken);
}

public sealed partial class PaddleLocalOcrRecognizer : ILocalOcrRecognizer
{
    private readonly PaddleOcrAll _engine;
    private bool _disposed;

    public PaddleLocalOcrRecognizer(string languageCode = "en")
    {
        var model = LocalLanguageCodes.NormalizeSource(languageCode) switch
        {
            "zh" => LocalFullModels.ChineseV5,
            "en" => LocalFullModels.EnglishV5,
            _ => throw new LocalModelException(
                "OCR_LANGUAGE_UNSUPPORTED",
                "PaddleOCR hiện hỗ trợ phụ đề tiếng Trung hoặc tiếng Anh."),
        };
        _engine = new PaddleOcrAll(
            model,
            PaddleDevice.OneDnn(
                cacheCapacity: 10,
                cpuMathThreadCount: Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
                memoryOptimized: true,
                glogEnabled: false))
        {
            AllowRotateDetection = true,
            Enable180Classification = false,
        };
    }

    public Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(imagePath))
        {
            throw new LocalModelException("OCR_IMAGE_MISSING", "Không tìm thấy khung hình OCR.");
        }

        return Task.Run<IReadOnlyList<OcrTextLine>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
                if (image.Empty())
                {
                    throw new LocalModelException("OCR_IMAGE_INVALID", "Khung hình OCR không hợp lệ.");
                }

                var result = _engine.Run(image);
                cancellationToken.ThrowIfCancellationRequested();
                return result.Regions
                    .Where(region => region.Score >= 0.45f)
                    .Select(region => new OcrTextLine(
                        WhitespaceRegex().Replace(region.Text, " ").Trim(),
                        region.Score))
                    .Where(line => line.Text.Length > 0)
                    .ToArray();
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
                    "OCR_RECOGNITION_FAILED",
                    "Không thể nhận dạng phụ đề cứng bằng PaddleOCR local.",
                    exception);
            }
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _engine.Dispose();
        _disposed = true;
    }

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();
}
