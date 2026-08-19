using System.Buffers.Binary;
using System.Text;

namespace SubVid.App.Jobs;

public sealed record VoiceActivityAnalysis(
    double RawDurationSeconds,
    double VoiceStartSeconds,
    double VoiceEndSeconds,
    double TrimStartSeconds,
    double TrimEndSeconds,
    double LeadingSilenceSeconds,
    double TrailingSilenceSeconds,
    double PlayableDurationSeconds,
    bool IsReliable)
{
    public static VoiceActivityAnalysis UseWholeFile(double durationSeconds)
    {
        var duration = double.IsFinite(durationSeconds) ? Math.Max(0, durationSeconds) : 0;
        return new VoiceActivityAnalysis(
            duration,
            0,
            duration,
            0,
            duration,
            0,
            0,
            duration,
            false);
    }
}

/// <summary>
/// Detects leading and trailing silence in uncompressed PCM WAV files. The detector deliberately
/// keeps a small amount of room around speech so timeline fitting never clips consonants.
/// Unsupported encodings fall back to the complete file instead of failing voice generation.
/// </summary>
public static class VoiceActivityAnalyzer
{
    private const double AbsoluteThreshold = 0.0025;
    private const double RelativeThreshold = 0.03;
    private const double MinimumPeak = 0.001;
    private const double LeadingRoomSeconds = 0.06;
    private const double TrailingRoomSeconds = 0.12;
    private const int AnalysisWindowMilliseconds = 10;
    private const int MinimumConsecutiveActiveWindows = 2;

    public static VoiceActivityAnalysis Analyze(string path, double fallbackDurationSeconds)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            if (ReadFourCc(reader) != "RIFF")
            {
                return VoiceActivityAnalysis.UseWholeFile(fallbackDurationSeconds);
            }

            _ = reader.ReadUInt32();
            if (ReadFourCc(reader) != "WAVE")
            {
                return VoiceActivityAnalysis.UseWholeFile(fallbackDurationSeconds);
            }

            ushort audioFormat = 0;
            ushort channels = 0;
            int sampleRate = 0;
            ushort blockAlign = 0;
            ushort bitsPerSample = 0;
            long dataOffset = 0;
            long dataSize = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                var chunkId = ReadFourCc(reader);
                var chunkSize = reader.ReadUInt32();
                var chunkDataStart = stream.Position;
                var next = Math.Min(stream.Length, chunkDataStart + chunkSize + (chunkSize % 2));
                if (chunkId == "fmt " && chunkSize >= 16)
                {
                    audioFormat = reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    sampleRate = reader.ReadInt32();
                    _ = reader.ReadUInt32();
                    blockAlign = reader.ReadUInt16();
                    bitsPerSample = reader.ReadUInt16();
                }
                else if (chunkId == "data")
                {
                    dataOffset = chunkDataStart;
                    dataSize = Math.Min(chunkSize, stream.Length - chunkDataStart);
                }

                stream.Position = next;
            }

            if (audioFormat != 1
                || bitsPerSample != 16
                || channels == 0
                || sampleRate <= 0
                || blockAlign < channels * sizeof(short)
                || dataOffset <= 0
                || dataSize < blockAlign)
            {
                return VoiceActivityAnalysis.UseWholeFile(fallbackDurationSeconds);
            }

            var frameCount = dataSize / blockAlign;
            var rawDuration = frameCount / (double)sampleRate;
            var windowFrames = Math.Max(1, sampleRate * AnalysisWindowMilliseconds / 1_000);
            var windowCount = (int)Math.Ceiling(frameCount / (double)windowFrames);
            var peaks = new double[windowCount];
            stream.Position = dataOffset;
            var sampleBuffer = new byte[sizeof(short)];
            double overallPeak = 0;
            for (long frame = 0; frame < frameCount; frame++)
            {
                double framePeak = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    if (reader.Read(sampleBuffer, 0, sampleBuffer.Length) != sampleBuffer.Length)
                    {
                        return VoiceActivityAnalysis.UseWholeFile(rawDuration);
                    }

                    var sample = BinaryPrimitives.ReadInt16LittleEndian(sampleBuffer);
                    framePeak = Math.Max(framePeak, Math.Abs(sample / 32768d));
                }

                var remainingBytes = blockAlign - channels * sizeof(short);
                if (remainingBytes > 0)
                {
                    stream.Seek(remainingBytes, SeekOrigin.Current);
                }

                var windowIndex = (int)(frame / windowFrames);
                peaks[windowIndex] = Math.Max(peaks[windowIndex], framePeak);
                overallPeak = Math.Max(overallPeak, framePeak);
            }

            if (overallPeak < MinimumPeak)
            {
                return VoiceActivityAnalysis.UseWholeFile(rawDuration);
            }

            var threshold = Math.Max(AbsoluteThreshold, overallPeak * RelativeThreshold);
            var firstActiveWindow = FindFirstActiveRun(peaks, threshold);
            var lastActiveWindow = FindLastActiveRun(peaks, threshold);
            if (firstActiveWindow < 0 || lastActiveWindow < firstActiveWindow)
            {
                return VoiceActivityAnalysis.UseWholeFile(rawDuration);
            }

            var windowSeconds = windowFrames / (double)sampleRate;
            var voiceStart = Math.Clamp(firstActiveWindow * windowSeconds, 0, rawDuration);
            var voiceEnd = Math.Clamp((lastActiveWindow + 1) * windowSeconds, voiceStart, rawDuration);
            var trimStart = Math.Max(0, voiceStart - LeadingRoomSeconds);
            var trimEnd = Math.Min(rawDuration, voiceEnd + TrailingRoomSeconds);
            return new VoiceActivityAnalysis(
                rawDuration,
                voiceStart,
                voiceEnd,
                trimStart,
                trimEnd,
                voiceStart,
                Math.Max(0, rawDuration - voiceEnd),
                Math.Max(0, trimEnd - trimStart),
                true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return VoiceActivityAnalysis.UseWholeFile(fallbackDurationSeconds);
        }
    }

    private static int FindFirstActiveRun(IReadOnlyList<double> peaks, double threshold)
    {
        var run = 0;
        for (var index = 0; index < peaks.Count; index++)
        {
            run = peaks[index] >= threshold ? run + 1 : 0;
            if (run >= MinimumConsecutiveActiveWindows)
            {
                return index - run + 1;
            }
        }

        return -1;
    }

    private static int FindLastActiveRun(IReadOnlyList<double> peaks, double threshold)
    {
        var run = 0;
        for (var index = peaks.Count - 1; index >= 0; index--)
        {
            run = peaks[index] >= threshold ? run + 1 : 0;
            if (run >= MinimumConsecutiveActiveWindows)
            {
                return index + run - 1;
            }
        }

        return -1;
    }

    private static string ReadFourCc(BinaryReader reader) => new(reader.ReadChars(4));
}
