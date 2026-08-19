namespace BilibiliDownloader.Application.Interfaces;

public interface IFFmpegService
{
    Task<string> MergeVideoAudioAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        CancellationToken cancellationToken);

    Task<(bool IsValid, string Message)> ValidateAsync(string? configuredPath, CancellationToken cancellationToken);
}
