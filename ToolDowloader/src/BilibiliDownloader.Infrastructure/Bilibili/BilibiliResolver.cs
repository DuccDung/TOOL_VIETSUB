using System.Text.Json;
using BilibiliDownloader.Application.DTOs;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Domain.Enums;
using BilibiliDownloader.Domain.Models;

namespace BilibiliDownloader.Infrastructure.Bilibili;

public sealed class BilibiliResolver(BilibiliClient client) : IBilibiliResolver
{
    public async Task<BilibiliVideoDto> ResolveAsync(
        BilibiliUrlInfo urlInfo,
        CancellationToken cancellationToken)
    {
        using var viewDocument = await client.GetVideoViewAsync(urlInfo.VideoId, cancellationToken).ConfigureAwait(false);
        var viewData = GetSuccessfulData(viewDocument.RootElement, "Không thể lấy thông tin video.");

        var pages = viewData.GetProperty("pages");
        if (urlInfo.PageNumber > pages.GetArrayLength())
        {
            throw new AppException(AppErrorCode.VideoNotFound, "Phần video được yêu cầu không tồn tại.");
        }

        var page = pages[urlInfo.PageNumber - 1];
        var cid = page.GetProperty("cid").GetInt64();
        var baseTitle = GetString(viewData, "title", urlInfo.VideoId);
        var partTitle = GetString(page, "part", string.Empty);
        var title = urlInfo.PageNumber > 1 && !string.IsNullOrWhiteSpace(partTitle)
            ? $"{baseTitle} - {partTitle}"
            : baseTitle;

        using var playDocument = await client.GetPlayUrlAsync(urlInfo.VideoId, cid, cancellationToken).ConfigureAwait(false);
        var playData = GetSuccessfulData(playDocument.RootElement, "Video không cung cấp luồng phát hợp lệ.");
        var streams = ParseStreams(playData);
        if (streams.Count == 0)
        {
            throw new AppException(AppErrorCode.VideoNotAvailable, "Video không cung cấp stream có thể tải xuống công khai.");
        }

        var owner = viewData.GetProperty("owner");
        var durationSeconds = page.TryGetProperty("duration", out var durationElement)
            ? durationElement.GetInt32()
            : viewData.GetProperty("duration").GetInt32();
        var thumbnail = NormalizeHttps(GetString(viewData, "pic", string.Empty));

        return new BilibiliVideoDto
        {
            Id = urlInfo.VideoId,
            SourceUrl = urlInfo.NormalizedUrl!,
            Title = title,
            Author = GetString(owner, "name", "Unknown"),
            ThumbnailUrl = thumbnail,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            PageNumber = urlInfo.PageNumber,
            Streams = streams
        };
    }

    private static JsonElement GetSuccessfulData(JsonElement root, string fallbackMessage)
    {
        var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : -1;
        if (code == 0 && root.TryGetProperty("data", out var data))
        {
            return data;
        }

        var message = root.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString()
            : fallbackMessage;
        var errorCode = code == -404 ? AppErrorCode.VideoNotFound : AppErrorCode.VideoNotAvailable;
        throw new AppException(errorCode, string.IsNullOrWhiteSpace(message) ? fallbackMessage : message);
    }

    private static IReadOnlyList<BilibiliStreamDto> ParseStreams(JsonElement data)
    {
        if (!data.TryGetProperty("dash", out var dash) ||
            !dash.TryGetProperty("video", out var videos) ||
            videos.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var audio = SelectAudio(dash);
        return videos.EnumerateArray()
            .Select(video => new
            {
                Element = video,
                Id = GetInt(video, "id"),
                Width = GetInt(video, "width"),
                Height = GetInt(video, "height"),
                Bandwidth = GetInt(video, "bandwidth"),
                Codec = GetString(video, "codecs", string.Empty),
                CodecPriority = CodecPriority(GetString(video, "codecs", string.Empty))
            })
            .Where(video => video.Height > 0 && video.Id > 0)
            .GroupBy(video => video.Height)
            .Select(group => group
                .OrderBy(video => video.CodecPriority)
                .ThenByDescending(video => video.Bandwidth)
                .First())
            .OrderByDescending(video => video.Height)
            .Select(video => new BilibiliStreamDto
            {
                Id = $"{video.Id}:{video.Codec}",
                QualityId = video.Id,
                Width = video.Width,
                Height = video.Height,
                Quality = GetQualityName(video.Id, video.Height),
                VideoUrl = NormalizeHttps(GetBaseUrl(video.Element)),
                AudioUrl = audio.Url,
                VideoCodec = video.Codec,
                AudioCodec = audio.Codec
            })
            .Where(stream => Uri.TryCreate(stream.VideoUrl, UriKind.Absolute, out _))
            .ToArray();
    }

    private static (string? Url, string? Codec) SelectAudio(JsonElement dash)
    {
        if (!dash.TryGetProperty("audio", out var audios) || audios.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        var selected = audios.EnumerateArray()
            .OrderByDescending(audio => GetInt(audio, "bandwidth"))
            .FirstOrDefault();
        return selected.ValueKind == JsonValueKind.Undefined
            ? (null, null)
            : (NormalizeHttps(GetBaseUrl(selected)), GetString(selected, "codecs", string.Empty));
    }

    private static string GetBaseUrl(JsonElement element)
    {
        if (element.TryGetProperty("baseUrl", out var baseUrl))
        {
            return baseUrl.GetString() ?? string.Empty;
        }

        return element.TryGetProperty("base_url", out var alternate)
            ? alternate.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static string GetString(JsonElement element, string propertyName, string fallback) =>
        element.TryGetProperty(propertyName, out var value) ? value.GetString() ?? fallback : fallback;

    private static int CodecPriority(string codec) => codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase) ? 0 :
        codec.StartsWith("hev", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    private static string NormalizeHttps(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? $"https://{url[7..]}" : url;

    private static string GetQualityName(int qualityId, int height) => qualityId switch
    {
        127 => "8K",
        126 => "Dolby Vision",
        125 => "HDR",
        120 => "4K",
        116 => "1080P60",
        112 => "1080P+",
        80 => "1080P",
        74 => "720P60",
        64 => "720P",
        32 => "480P",
        16 => "360P",
        6 => "240P",
        _ => $"{height}P"
    };
}
