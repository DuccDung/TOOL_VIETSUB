using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Application.Services;

public sealed class QualitySelectionService : IQualitySelectionService
{
    public BilibiliStreamDto SelectBest(IReadOnlyList<BilibiliStreamDto> streams, VideoQuality preferredQuality)
    {
        if (streams.Count == 0)
        {
            throw new AppException(AppErrorCode.VideoNotAvailable, "Video không có chất lượng tải xuống phù hợp.");
        }

        var ordered = streams
            .OrderByDescending(stream => stream.Height)
            .ThenByDescending(stream => stream.Width)
            .ToArray();

        if (preferredQuality == VideoQuality.BestAvailable)
        {
            return ordered[0];
        }

        var targetHeight = (int)preferredQuality;
        return ordered.FirstOrDefault(stream => stream.Height <= targetHeight) ?? ordered[^1];
    }
}
