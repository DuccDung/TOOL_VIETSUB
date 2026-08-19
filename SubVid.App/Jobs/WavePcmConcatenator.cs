using System.Buffers.Binary;
using System.Text;

namespace SubVid.App.Jobs;

internal static class WavePcmConcatenator
{
    private const int SampleRate = 48_000;
    private const short Channels = 2;
    private const short BitsPerSample = 16;

    public static async Task ConcatenateAsync(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (inputPaths.Count == 0)
        {
            throw new ArgumentException("Cần ít nhất một WAV để ghép.", nameof(inputPaths));
        }

        var chunks = inputPaths.Select(FindPcmData).ToArray();
        var totalBytes = chunks.Sum(chunk => (long)chunk.Length);
        if (totalBytes > uint.MaxValue - 36)
        {
            throw new LocalJobException(
                "VOICE_TIMELINE_TOO_LARGE",
                "Timeline giọng vượt giới hạn WAV 4 GB. Hãy xuất theo nhiều phần.",
                retryable: false);
        }

        await using var output = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(BuildHeader((uint)totalBytes), cancellationToken);
        foreach (var chunk in chunks)
        {
            await using var input = new FileStream(
                chunk.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            input.Position = chunk.Offset;
            await CopyExactlyAsync(input, output, chunk.Length, cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
    }

    private static PcmDataChunk FindPcmData(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
        {
            throw InvalidWave();
        }

        _ = reader.ReadUInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
        {
            throw InvalidWave();
        }

        var formatValidated = false;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = reader.ReadUInt32();
            var dataOffset = stream.Position;
            if (id == "fmt ")
            {
                if (length < 16)
                {
                    throw InvalidWave();
                }

                var format = reader.ReadUInt16();
                var channels = reader.ReadUInt16();
                var sampleRate = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt16();
                var bits = reader.ReadUInt16();
                if (format != 1 || channels != Channels || sampleRate != SampleRate || bits != BitsPerSample)
                {
                    throw new LocalJobException(
                        "VOICE_WAVE_FORMAT_INVALID",
                        "Các phần timeline giọng không cùng định dạng PCM 48 kHz stereo.",
                        retryable: false);
                }

                formatValidated = true;
            }
            else if (id == "data")
            {
                if (!formatValidated || dataOffset + length > stream.Length)
                {
                    throw InvalidWave();
                }

                return new PcmDataChunk(path, dataOffset, length);
            }

            stream.Position = dataOffset + length + (length % 2);
        }

        throw InvalidWave();
    }

    private static byte[] BuildHeader(uint dataLength)
    {
        var header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 36 + dataLength);
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), (ushort)Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), SampleRate);
        var blockAlign = (ushort)(Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)(SampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), (ushort)BitsPerSample);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), dataLength);
        return header;
    }

    private static async Task CopyExactlyAsync(
        Stream input,
        Stream output,
        long length,
        CancellationToken cancellationToken)
    {
        var remaining = length;
        var buffer = new byte[(int)Math.Min(1024 * 1024, Math.Max(4096, length))];
        while (remaining > 0)
        {
            var read = await input.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0)
            {
                throw InvalidWave();
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static LocalJobException InvalidWave() => new(
        "VOICE_WAVE_INVALID",
        "Không thể đọc dữ liệu PCM của một phần timeline giọng.",
        retryable: false);

    private sealed record PcmDataChunk(string Path, long Offset, long Length);
}
