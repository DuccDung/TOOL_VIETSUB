using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace BilibiliDownloader.Application.Services;

public sealed class BilibiliService(
    IBilibiliUrlParser urlParser,
    IBilibiliResolver resolver,
    ILogger<BilibiliService> logger) : IBilibiliService
{
    public async Task<BilibiliVideoDto> AnalyzeAsync(string url, CancellationToken cancellationToken)
    {
        var urlInfo = urlParser.Parse(url);
        if (!urlInfo.IsValid)
        {
            throw new AppException(AppErrorCode.InvalidUrl, urlInfo.Error ?? "URL Bilibili không hợp lệ.");
        }

        logger.LogInformation("Analyze started for video {VideoId}", urlInfo.VideoId);
        var result = await resolver.ResolveAsync(urlInfo, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Analyze completed for video {VideoId} with {StreamCount} streams",
            urlInfo.VideoId,
            result.Streams.Count);
        return result;
    }
}
