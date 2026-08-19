using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Domain.Models;

public sealed record DownloadJobSnapshot(
    Guid Id,
    string VideoId,
    string Title,
    string Quality,
    string Format,
    DownloadStatus Status,
    DownloadStage Stage,
    long DownloadedBytes,
    long? TotalBytes,
    double Percentage,
    double SpeedBytesPerSecond,
    TimeSpan? RemainingTime,
    string? OutputPath,
    string? ErrorMessage);
