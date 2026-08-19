using BilibiliDownloader.Application.DTOs;

namespace BilibiliDownloader.Application.Interfaces;

public interface IBilibiliService
{
    Task<BilibiliVideoDto> AnalyzeAsync(string url, CancellationToken cancellationToken);
}
