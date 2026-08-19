namespace BilibiliDownloader.Application.DTOs;

public sealed record DownloadRequestDto
{
    public Guid JobId { get; init; }
    public required string VideoId { get; init; }
    public required string SourceUrl { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }
    public required string ThumbnailUrl { get; init; }
    public required BilibiliStreamDto Stream { get; init; }
    public required string OutputDirectory { get; init; }
    public string Format { get; init; } = "MP4";
    public string? OutputPath { get; init; }
}
