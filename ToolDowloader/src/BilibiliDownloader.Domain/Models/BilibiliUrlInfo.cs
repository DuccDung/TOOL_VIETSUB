namespace BilibiliDownloader.Domain.Models;

public sealed record BilibiliUrlInfo(
    string VideoId,
    bool IsValid,
    int PageNumber = 1,
    string? NormalizedUrl = null,
    string? Error = null)
{
    public static BilibiliUrlInfo Invalid(string error) => new(string.Empty, false, 1, null, error);
}
