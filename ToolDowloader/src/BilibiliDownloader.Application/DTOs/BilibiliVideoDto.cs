namespace BilibiliDownloader.Application.DTOs;

public sealed record BilibiliVideoDto
{
    public required string Id { get; init; }
    public required string SourceUrl { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }
    public required string ThumbnailUrl { get; init; }
    public required TimeSpan Duration { get; init; }
    public int PageNumber { get; init; } = 1;
    public IReadOnlyList<BilibiliStreamDto> Streams { get; init; } = [];
}
