using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Application.Interfaces;

public interface IQualitySelectionService
{
    BilibiliStreamDto SelectBest(IReadOnlyList<BilibiliStreamDto> streams, VideoQuality preferredQuality);
}
