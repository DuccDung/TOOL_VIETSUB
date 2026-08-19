namespace BilibiliDownloader.Infrastructure.Configuration;

public sealed class BilibiliOptions
{
    public const string SectionName = "Bilibili";

    public string ApiBaseUrl { get; set; } = "https://api.bilibili.com/";
    public string WebBaseUrl { get; set; } = "https://www.bilibili.com/";
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaximumResponseBytes { get; set; } = 4 * 1024 * 1024;
}
