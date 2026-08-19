namespace BilibiliDownloader.Application.Interfaces;

public interface IThumbnailService
{
    Task<byte[]> DownloadAsync(string thumbnailUrl, CancellationToken cancellationToken);
}
