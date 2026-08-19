using System.Text;
using SubVid.App.Jobs;

namespace SubVid.App.Tests;

public sealed class VoiceActivityAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SUBVID_VOICE_ACTIVITY_TEST",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Analyze_WithLeadingAndTrailingSilence_ReturnsSpeechSafeTrimBounds()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "speech.wav");
        File.WriteAllBytes(path, CreateWave(16_000, 200, 500, 300));

        var result = VoiceActivityAnalyzer.Analyze(path, 1);

        Assert.True(result.IsReliable);
        Assert.InRange(result.LeadingSilenceSeconds, 0.19, 0.21);
        Assert.InRange(result.TrailingSilenceSeconds, 0.29, 0.31);
        Assert.InRange(result.TrimStartSeconds, 0.13, 0.15);
        Assert.InRange(result.TrimEndSeconds, 0.81, 0.83);
        Assert.InRange(result.PlayableDurationSeconds, 0.66, 0.70);
    }

    [Fact]
    public void Analyze_WhenWaveContainsOnlySilence_FallsBackToWholeFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "silence.wav");
        File.WriteAllBytes(path, CreateWave(16_000, 1_000, 0, 0));

        var result = VoiceActivityAnalyzer.Analyze(path, 1);

        Assert.False(result.IsReliable);
        Assert.Equal(0, result.TrimStartSeconds);
        Assert.Equal(1, result.TrimEndSeconds, 3);
        Assert.Equal(1, result.PlayableDurationSeconds, 3);
    }

    private static byte[] CreateWave(
        int sampleRate,
        int leadingSilenceMilliseconds,
        int voiceMilliseconds,
        int trailingSilenceMilliseconds)
    {
        var totalMilliseconds = leadingSilenceMilliseconds + voiceMilliseconds + trailingSilenceMilliseconds;
        var sampleCount = sampleRate * totalMilliseconds / 1_000;
        var leadingSamples = sampleRate * leadingSilenceMilliseconds / 1_000;
        var voiceSamples = sampleRate * voiceMilliseconds / 1_000;
        var dataSize = sampleCount * sizeof(short);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        for (var index = 0; index < sampleCount; index++)
        {
            var insideVoice = index >= leadingSamples && index < leadingSamples + voiceSamples;
            var sample = insideVoice
                ? (short)(Math.Sin(2 * Math.PI * 220 * index / sampleRate) * 12_000)
                : (short)0;
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
