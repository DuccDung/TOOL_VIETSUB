using BilibiliDownloader.Domain.Models;

namespace BilibiliDownloader.Application.Interfaces;

public interface IBilibiliUrlParser
{
    BilibiliUrlInfo Parse(string url);
}
