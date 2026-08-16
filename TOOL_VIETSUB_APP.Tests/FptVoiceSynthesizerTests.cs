using System.Net;
using System.Text;
using TOOL_VIETSUB_APP.Api;
using TOOL_VIETSUB_APP.Core;
using TOOL_VIETSUB_APP.Jobs;
using TOOL_VIETSUB_APP.LocalAi;

namespace TOOL_VIETSUB_APP.Tests;

public sealed class FptVoiceSynthesizerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "TOOL_VIETSUB_TESTS",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Submit_PollsAsyncUrlAndWritesValidWave()
    {
        var handler = new SequenceHandler(async (request, call) =>
        {
            if (call == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(FptVoiceSynthesizer.Endpoint, request.RequestUri?.ToString());
                Assert.Equal("test-fpt-api-key", Assert.Single(request.Headers.GetValues("api_key")));
                Assert.Equal("banmai", Assert.Single(request.Headers.GetValues("voice")));
                Assert.Equal("2", Assert.Single(request.Headers.GetValues("speed")));
                Assert.Equal("wav", Assert.Single(request.Headers.GetValues("format")));
                Assert.Equal("Xin chào", await request.Content!.ReadAsStringAsync());
                return JsonResponse(
                    "{\"async\":\"https://voice-test.s3-ap-southeast-1.amazonaws.com/result.wav\",\"error\":0,\"message\":\"accepted\",\"request_id\":\"request-1\"}");
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            return call == 2
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(CreateWave()),
                };
        });
        using var client = new HttpClient(handler);
        var outputPath = Path.Combine(_root, "voice.wav");
        var savedCheckpoint = (RequestId: (string?)null, ResultUrl: (string?)null);
        var synthesizer = new FptVoiceSynthesizer(
            client,
            "test-fpt-api-key",
            pollInterval: TimeSpan.Zero,
            pollTimeout: TimeSpan.FromSeconds(2),
            delay: (_, _) => Task.CompletedTask);

        await synthesizer.SynthesizeAsync(
            [new VoiceSynthesisRequest(
                Guid.NewGuid(),
                "Xin chào",
                outputPath,
                "fpt:banmai",
                2,
                new VoiceProviderCheckpoint(null, null, (requestId, resultUrl, _) =>
                {
                    savedCheckpoint = (requestId, resultUrl);
                    return ValueTask.CompletedTask;
                }))],
            CancellationToken.None);

        Assert.Equal(3, handler.CallCount);
        Assert.Equal("request-1", savedCheckpoint.RequestId);
        Assert.Contains("result.wav", savedCheckpoint.ResultUrl, StringComparison.Ordinal);
        Assert.True(File.Exists(outputPath));
        var wave = WaveFileMetadata.Read(outputPath);
        Assert.Equal(16_000, wave.SampleRate);
        Assert.Equal(1, wave.Channels);
    }

    [Fact]
    public async Task ExistingCheckpoint_DownloadsWithoutSubmittingAgain()
    {
        var handler = new SequenceHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(CreateWave()),
            });
        });
        using var client = new HttpClient(handler);
        var outputPath = Path.Combine(_root, "resumed.wav");
        var synthesizer = new FptVoiceSynthesizer(
            client,
            "test-fpt-api-key",
            pollInterval: TimeSpan.Zero,
            delay: (_, _) => Task.CompletedTask);

        await synthesizer.SynthesizeAsync(
            [new VoiceSynthesisRequest(
                Guid.NewGuid(),
                "Tiếp tục",
                outputPath,
                "fpt:leminh",
                0,
                new VoiceProviderCheckpoint(
                    "request-existing",
                    "https://voice-test.s3-ap-southeast-1.amazonaws.com/existing.wav"))],
            CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public async Task VeryShortCue_IsPaddedToProviderMinimum()
    {
        string? submittedText = null;
        var handler = new SequenceHandler(async (request, call) =>
        {
            if (call == 1)
            {
                submittedText = await request.Content!.ReadAsStringAsync();
                return JsonResponse(
                    "{\"async\":\"https://voice-test.s3-ap-southeast-1.amazonaws.com/short.wav\",\"error\":0,\"request_id\":\"short-1\"}");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(CreateWave()),
            };
        });
        using var client = new HttpClient(handler);
        var synthesizer = new FptVoiceSynthesizer(
            client,
            "test-fpt-api-key",
            pollInterval: TimeSpan.Zero,
            delay: (_, _) => Task.CompletedTask);

        await synthesizer.SynthesizeAsync(
            [new VoiceSynthesisRequest(Guid.NewGuid(), "Ừ", Path.Combine(_root, "short.wav"), "fpt:lannhi")],
            CancellationToken.None);

        Assert.Equal(3, submittedText?.Length);
        Assert.StartsWith("Ừ", submittedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnauthorizedResponse_ReturnsFriendlyNonRetryableCode()
    {
        var handler = new SequenceHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var client = new HttpClient(handler);
        var synthesizer = new FptVoiceSynthesizer(client, "test-fpt-api-key");

        var exception = await Assert.ThrowsAsync<VoiceSynthesisException>(() =>
            synthesizer.SynthesizeAsync(
                [new VoiceSynthesisRequest(Guid.NewGuid(), "Xin chào", Path.Combine(_root, "bad.wav"), "fpt:banmai")],
                CancellationToken.None));

        Assert.Equal("FPT_API_KEY_INVALID", exception.Code);
        Assert.False(exception.Retryable);
    }

    [Fact]
    public void CredentialStore_EncryptsAndDeletesFptKey()
    {
        var paths = new AppPaths(_root);
        var store = new ProtectedVoiceCredentialStore(paths);

        store.SaveFptKey("secret-fpt-api-key-value");

        Assert.True(store.HasFptKey);
        Assert.Equal("secret-fpt-api-key-value", store.GetFptKey());
        var raw = File.ReadAllBytes(Path.Combine(paths.RootDirectory, "voice.credentials"));
        Assert.DoesNotContain("secret-fpt-api-key-value", Encoding.UTF8.GetString(raw));

        store.DeleteFptKey();
        Assert.False(store.HasFptKey);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static byte[] CreateWave()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        const int sampleRate = 16_000;
        const int dataSize = 3_200;
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
        writer.Write(new byte[dataSize]);
        writer.Flush();
        return stream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class SequenceHandler(
        Func<HttpRequestMessage, int, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return responder(request, CallCount);
        }
    }
}
