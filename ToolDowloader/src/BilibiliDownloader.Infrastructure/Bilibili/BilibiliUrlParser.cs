using System.Text.RegularExpressions;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Models;

namespace BilibiliDownloader.Infrastructure.Bilibili;

public sealed partial class BilibiliUrlParser : IBilibiliUrlParser
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "bilibili.com",
        "www.bilibili.com"
    };

    public BilibiliUrlInfo Parse(string url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return BilibiliUrlInfo.Invalid("Vui lòng nhập URL Bilibili đầy đủ.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return BilibiliUrlInfo.Invalid("URL Bilibili phải sử dụng HTTPS.");
        }

        if (!AllowedHosts.Contains(uri.IdnHost) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return BilibiliUrlInfo.Invalid("Tên miền Bilibili không được hỗ trợ.");
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 2 ||
            !string.Equals(segments[0], "video", StringComparison.OrdinalIgnoreCase) ||
            !BvidPattern().IsMatch(segments[1]))
        {
            return BilibiliUrlInfo.Invalid("URL phải có dạng https://www.bilibili.com/video/BVxxxxxxxxxx.");
        }

        var pageNumber = 1;
        if (!TryParseQuery(uri.Query, ref pageNumber, out var queryError))
        {
            return BilibiliUrlInfo.Invalid(queryError!);
        }

        var videoId = segments[1];
        var normalized = pageNumber == 1
            ? $"https://www.bilibili.com/video/{videoId}/"
            : $"https://www.bilibili.com/video/{videoId}/?p={pageNumber}";
        return new BilibiliUrlInfo(videoId, true, pageNumber, normalized);
    }

    private static bool TryParseQuery(string query, ref int pageNumber, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            if (!string.Equals(key, "p", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parts.Length != 2 || !int.TryParse(parts[1], out pageNumber) || pageNumber is < 1 or > 9999)
            {
                error = "Số phần video trong tham số p không hợp lệ.";
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex("^BV[0-9A-Za-z]{10}$", RegexOptions.CultureInvariant)]
    private static partial Regex BvidPattern();
}
