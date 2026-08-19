using System.Net;
using System.Net.Sockets;
using BilibiliDownloader.Application.Errors;
using BilibiliDownloader.Domain.Enums;

namespace BilibiliDownloader.Infrastructure.Bilibili;

public interface IRemoteUriValidator
{
    Task<Uri> ValidateMediaAsync(string url, CancellationToken cancellationToken);
    Task<Uri> ValidateImageAsync(string url, CancellationToken cancellationToken);
}

public sealed class SafeRemoteUriValidator : IRemoteUriValidator
{
    private static readonly string[] MediaHostSuffixes =
    [
        ".bilivideo.com",
        ".bilivideo.cn",
        ".bilibili.com",
        ".akamaized.net"
    ];

    private static readonly string[] ImageHostSuffixes =
    [
        ".hdslb.com",
        ".bilibili.com"
    ];

    public Task<Uri> ValidateMediaAsync(string url, CancellationToken cancellationToken) =>
        ValidateAsync(url, MediaHostSuffixes, cancellationToken);

    public Task<Uri> ValidateImageAsync(string url, CancellationToken cancellationToken) =>
        ValidateAsync(url, ImageHostSuffixes, cancellationToken);

    private static async Task<Uri> ValidateAsync(
        string url,
        IReadOnlyCollection<string> allowedSuffixes,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            IPAddress.TryParse(uri.IdnHost, out _) ||
            !allowedSuffixes.Any(suffix => uri.IdnHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AppException(AppErrorCode.NetworkError, "Media URL không thuộc nguồn Bilibili được hỗ trợ.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            throw new AppException(AppErrorCode.NetworkError, "Không thể phân giải máy chủ media.", exception);
        }

        if (addresses.Length == 0 || addresses.Any(IsPrivateOrReserved))
        {
            throw new AppException(AppErrorCode.NetworkError, "Media URL trỏ tới địa chỉ mạng không an toàn.");
        }

        return uri;
    }

    private static bool IsPrivateOrReserved(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var ipv6Bytes = address.GetAddressBytes();
            return (ipv6Bytes[0] & 0xFE) == 0xFC || address.Equals(IPAddress.IPv6None);
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 10 or 127 ||
               bytes[0] >= 224 ||
               bytes[0] == 169 && bytes[1] == 254 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }
}
