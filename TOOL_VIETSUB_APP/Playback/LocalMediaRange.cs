using System.Globalization;

namespace TOOL_VIETSUB_APP.Playback;

public readonly record struct MediaByteRange(long Start, long End)
{
    public long Length => End - Start + 1;
}

public static class LocalMediaRange
{
    public static bool TryParse(
        string? rangeHeader,
        long resourceLength,
        out MediaByteRange range)
    {
        range = default;
        if (resourceLength <= 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(rangeHeader))
        {
            range = new MediaByteRange(0, resourceLength - 1);
            return true;
        }

        const string prefix = "bytes=";
        if (!rangeHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = rangeHeader[prefix.Length..].Trim();
        if (value.Contains(','))
        {
            return false;
        }

        var separator = value.IndexOf('-');
        if (separator < 0)
        {
            return false;
        }

        var startText = value[..separator].Trim();
        var endText = value[(separator + 1)..].Trim();
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffixLength)
                || suffixLength <= 0)
            {
                return false;
            }

            suffixLength = Math.Min(suffixLength, resourceLength);
            range = new MediaByteRange(resourceLength - suffixLength, resourceLength - 1);
            return true;
        }

        if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || start < 0
            || start >= resourceLength)
        {
            return false;
        }

        var end = resourceLength - 1;
        if (endText.Length > 0
            && (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out end)
                || end < start))
        {
            return false;
        }

        range = new MediaByteRange(start, Math.Min(end, resourceLength - 1));
        return true;
    }
}

public sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;

    public BoundedReadStream(Stream inner, long start, long length)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead || !inner.CanSeek)
        {
            throw new ArgumentException("Stream must be readable and seekable.", nameof(inner));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (start + length > inner.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _inner = inner;
        _start = start;
        _length = length;
        _inner.Position = start;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _inner.Position - _start;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var allowed = GetAllowedCount(count);
        return allowed == 0 ? 0 : _inner.Read(buffer, offset, allowed);
    }

    public override int Read(Span<byte> buffer)
    {
        var allowed = GetAllowedCount(buffer.Length);
        return allowed == 0 ? 0 : _inner.Read(buffer[..allowed]);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var allowed = GetAllowedCount(buffer.Length);
        return allowed == 0
            ? 0
            : await _inner.ReadAsync(buffer[..allowed], cancellationToken);
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var allowed = GetAllowedCount(count);
        return allowed == 0
            ? Task.FromResult(0)
            : _inner.ReadAsync(buffer, offset, allowed, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0 || target > _length)
        {
            throw new IOException("Seek position is outside the media range.");
        }

        _inner.Position = _start + target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int GetAllowedCount(int requested) =>
        (int)Math.Min(Math.Max(0, _length - Position), requested);
}
