using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Domain.Models;

namespace BilibiliDownloader.Application.Interfaces;

public interface IBilibiliResolver
{
    Task<BilibiliVideoDto> ResolveAsync(BilibiliUrlInfo urlInfo, CancellationToken cancellationToken);
}
