namespace BilibiliDownloader.Application.DTOs;

public sealed record BilibiliStreamDto
{
    public required string Id { get; init; }
    public required int QualityId { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string Quality { get; init; }
    public required string VideoUrl { get; init; }
    public string? AudioUrl { get; init; }
    public long? FileSize { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }

    public override string ToString() => FileSize is > 0
        ? $"{Quality} ({Width}×{Height}, {FormatBytes(FileSize.Value)})"
        : $"{Quality} ({Width}×{Height})";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
