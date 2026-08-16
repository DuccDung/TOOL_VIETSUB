using System.Collections.Concurrent;
using System.Security.Cryptography;
using SubVid.App.Core;

namespace SubVid.App.LocalAi;

public sealed record LocalModelFile(
    string RelativePath,
    Uri DownloadUri,
    long ExpectedSizeBytes,
    string? ExpectedSha256 = null);

public sealed record LocalModelDescriptor(
    string Id,
    string Engine,
    string DisplayName,
    string Version,
    string License,
    IReadOnlyList<LocalModelFile> Files);

public sealed record LocalModelStatus(
    string Id,
    string Engine,
    string DisplayName,
    string Version,
    string License,
    bool Ready,
    long InstalledBytes,
    long RequiredBytes);

public sealed record LocalModelDownloadProgress(
    string ModelId,
    string FileName,
    long BytesProcessed,
    long TotalBytes,
    double Percent);

public sealed class LocalModelException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed class LocalModelManager : IDisposable
{
    private static readonly IReadOnlyList<LocalModelDescriptor> DefaultCatalog =
    [
        new(
            "whisper-base-multilingual",
            "WHISPER",
            "Whisper Base · Đa ngôn ngữ",
            "whisper.cpp-base",
            "MIT (engine); model license follows OpenAI Whisper",
            [
                new(
                    Path.Combine("whisper", "ggml-base.bin"),
                    new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin?download=true"),
                    147_951_465,
                    "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
            ]),
        new(
            "piper-vi-vais1000-medium",
            "PIPER",
            "Piper · Nữ tiếng Việt",
            "vi_VN-vais1000-medium@ea046e8",
            "VAIS-1000 voice data CC BY 4.0; Piper runtime GPL-3.0",
            [
                new(
                    Path.Combine("piper", "vi_VN-vais1000-medium.onnx"),
                    new Uri("https://huggingface.co/rhasspy/piper-voices/resolve/ea046e8458f6acd997706d6e6066a022b42f6fb1/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium.onnx?download=true"),
                    63_201_294,
                    "ec7c89e2c85f4d1edc24b6120c18aaf1bda614f06b511567eb9c7c0de15e2dab"),
                new(
                    Path.Combine("piper", "vi_VN-vais1000-medium.onnx.json"),
                    new Uri("https://huggingface.co/rhasspy/piper-voices/resolve/ea046e8458f6acd997706d6e6066a022b42f6fb1/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium.onnx.json?download=true"),
                    4_860,
                    "fafb9da1354ed4b77c31af228ed41fb41cd825c14cffa105454b25e6ae751ee0"),
            ]),
        new(
            "argos-en-vi",
            "ARGOS",
            "Argos English → Vietnamese",
            "1.9",
            "Argos MIT/CC0; OPUS-MT derived model CC BY 4.0",
            [
                new(
                    Path.Combine("argos", "translate-en_vi-1_9.argosmodel"),
                    new Uri("https://argos-net.com/v1/translate-en_vi-1_9.argosmodel"),
                    67_770_159,
                    "86957101aa4099aa9a1a7492e41987d938d3cf0fdaf4fb684c0797a9d567dd16"),
            ]),
        new(
            "opus-mt-zh-vi-official-v2",
            "TRANSFORMERS",
            "OPUS-MT Chinese → Vietnamese · Official",
            "67ea2dbfbaf13a16772a40346d3d72b59e591443",
            "Apache-2.0",
            [
                new(
                    Path.Combine("transformers", "opus-mt-zh-vi-official", "config.json"),
                    new Uri("https://huggingface.co/Helsinki-NLP/opus-mt-zh-vi/resolve/67ea2dbfbaf13a16772a40346d3d72b59e591443/config.json?download=true"),
                    1_394,
                    "e91f9d2a167ec7c9c16954f75b7890c3305e312921ffbeaa9faf65139c3c14f3"),
                new(
                    Path.Combine("transformers", "opus-mt-zh-vi-official", "generation_config.json"),
                    new Uri("https://huggingface.co/Helsinki-NLP/opus-mt-zh-vi/resolve/67ea2dbfbaf13a16772a40346d3d72b59e591443/generation_config.json?download=true"),
                    293,
                    "81f06422302b9f4666f20c6f20f43b904e38ccce9326a208f82ff97c2c9699e5"),
                new(
                    Path.Combine("transformers", "opus-mt-zh-vi-official", "pytorch_model.bin"),
                    new Uri("https://huggingface.co/Helsinki-NLP/opus-mt-zh-vi/resolve/67ea2dbfbaf13a16772a40346d3d72b59e591443/pytorch_model.bin?download=true"),
                    312_087_009,
                    "b4c3f9cecd138d230a9e4d258bc99e87fbc343003b4e83834ab4615d20b8ff22"),
                new(
                    Path.Combine("transformers", "opus-mt-zh-vi-official", "source.spm"),
                    new Uri("https://huggingface.co/Helsinki-NLP/opus-mt-zh-vi/resolve/67ea2dbfbaf13a16772a40346d3d72b59e591443/source.spm?download=true"),
                    750_285,
                    "d617117f90da08f471ef72c649401748a26e9ffb22289986379a9b44f7d8810e"),
                new(
                    Path.Combine("transformers", "opus-mt-zh-vi-official", "target.spm"),
                    new Uri("https://huggingface.co/Helsinki-NLP/opus-mt-zh-vi/resolve/67ea2dbfbaf13a16772a40346d3d72b59e591443/target.spm?download=true"),
                    766_197,
                    "eb082ee05298800b89d0cfa7101ef9e468b2554459a8e7635cb56cc17d0f3f02"),
                new(
                    Path.Combine("transformers", "opus-mt-zh-vi-official", "tokenizer_config.json"),
                    new Uri("https://huggingface.co/Helsinki-NLP/opus-mt-zh-vi/resolve/67ea2dbfbaf13a16772a40346d3d72b59e591443/tokenizer_config.json?download=true"),
                    44,
                    "fb51b353052bf45e5dfc943e8e202572e304eaabb7b6ead225aad95d1b6d1f6f"),
                new(
                    Path.Combine("transformers", "opus-mt-zh-vi-official", "vocab.json"),
                    new Uri("https://huggingface.co/Helsinki-NLP/opus-mt-zh-vi/resolve/67ea2dbfbaf13a16772a40346d3d72b59e591443/vocab.json?download=true"),
                    1_509_806,
                    "6a9dab7e1cb05a1d99f563d61b2f5bd277885b79c769a5d3f3ecf5139dcf5039"),
            ]),
    ];

    private static readonly HashSet<string> AllowedDownloadHosts =
        new(["huggingface.co", "cdn-lfs.hf.co", "cas-bridge.xethub.hf.co", "argos-net.com"], StringComparer.OrdinalIgnoreCase);
    private readonly AppPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IReadOnlyDictionary<string, LocalModelDescriptor> _catalog;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloadLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, VerifiedModelFile> _verifiedFiles = new(StringComparer.OrdinalIgnoreCase);

    public LocalModelManager(
        AppPaths paths,
        HttpClient? httpClient = null,
        IEnumerable<LocalModelDescriptor>? catalog = null)
    {
        _paths = paths;
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = httpClient is null;
        _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("SubVid-App/1.0");
        _catalog = (catalog ?? DefaultCatalog).ToDictionary(item => item.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<LocalModelStatus> GetStatuses() =>
        _catalog.Values
            .OrderBy(item => item.Engine, StringComparer.Ordinal)
            .ThenBy(item => item.DisplayName, StringComparer.Ordinal)
            .Select(GetStatus)
            .ToArray();

    public string RequireFile(string modelId, string relativePath)
    {
        var descriptor = Find(modelId);
        var file = descriptor.Files.SingleOrDefault(item =>
            string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new LocalModelException("MODEL_FILE_UNKNOWN", "Model không chứa file được yêu cầu.");
        var path = _paths.GetModelPath(file.RelativePath);
        if (!IsFileReady(file, path))
        {
            throw new LocalModelException(
                "MODEL_NOT_READY",
                $"Model {descriptor.DisplayName} chưa được tải đầy đủ.");
        }

        return path;
    }

    public async Task<LocalModelStatus> DownloadAsync(
        string modelId,
        IProgress<LocalModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var descriptor = Find(modelId);
        var gate = _downloadLocks.GetOrAdd(modelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = GetStatus(descriptor);
            if (current.Ready)
            {
                return current;
            }

            EnsureFreeSpace(descriptor.Files.Sum(item =>
                IsFileReady(item, _paths.GetModelPath(item.RelativePath)) ? 0 : item.ExpectedSizeBytes));
            var processedBefore = 0L;
            var totalBytes = descriptor.Files.Sum(item => item.ExpectedSizeBytes);
            foreach (var file in descriptor.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = _paths.GetModelPath(file.RelativePath);
                if (IsFileReady(file, path))
                {
                    processedBefore += file.ExpectedSizeBytes;
                    continue;
                }

                await DownloadFileAsync(
                    descriptor.Id,
                    file,
                    path,
                    processedBefore,
                    totalBytes,
                    progress,
                    cancellationToken);
                processedBefore += file.ExpectedSizeBytes;
            }

            return GetStatus(descriptor);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DownloadFileAsync(
        string modelId,
        LocalModelFile file,
        string destinationPath,
        long processedBefore,
        long totalBytes,
        IProgress<LocalModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (file.DownloadUri.Scheme != Uri.UriSchemeHttps
            || !AllowedDownloadHosts.Contains(file.DownloadUri.Host))
        {
            throw new LocalModelException("MODEL_SOURCE_BLOCKED", "Nguồn tải model không được phép.");
        }

        var partialPath = destinationPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            var fileProcessed = 0L;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                fileProcessed += read;
                Report(progress, modelId, file, processedBefore, fileProcessed, totalBytes);
            }

            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
            if (fileProcessed != file.ExpectedSizeBytes)
            {
                throw new LocalModelException(
                    "MODEL_SIZE_INVALID",
                    $"Dung lượng model {Path.GetFileName(file.RelativePath)} không hợp lệ.");
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (file.ExpectedSha256 is not null
                && !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(file.ExpectedSha256)))
            {
                throw new LocalModelException(
                    "MODEL_HASH_INVALID",
                    $"Checksum model {Path.GetFileName(file.RelativePath)} không hợp lệ.");
            }

            destination.Close();
            File.Move(partialPath, destinationPath, overwrite: true);
            Report(progress, modelId, file, processedBefore, fileProcessed, totalBytes);
        }
        catch (OperationCanceledException)
        {
            DeletePartial(partialPath);
            throw;
        }
        catch (LocalModelException)
        {
            DeletePartial(partialPath);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            DeletePartial(partialPath);
            throw new LocalModelException(
                "MODEL_DOWNLOAD_FAILED",
                "Không thể tải model local. Hãy kiểm tra mạng và dung lượng đĩa rồi thử lại.",
                exception);
        }
    }

    private LocalModelStatus GetStatus(LocalModelDescriptor descriptor)
    {
        var installedBytes = descriptor.Files.Sum(file =>
        {
            var path = _paths.GetModelPath(file.RelativePath);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        });
        return new LocalModelStatus(
            descriptor.Id,
            descriptor.Engine,
            descriptor.DisplayName,
            descriptor.Version,
            descriptor.License,
            descriptor.Files.All(file => IsFileReady(file, _paths.GetModelPath(file.RelativePath))),
            installedBytes,
            descriptor.Files.Sum(file => file.ExpectedSizeBytes));
    }

    private LocalModelDescriptor Find(string modelId) =>
        _catalog.TryGetValue(modelId, out var descriptor)
            ? descriptor
            : throw new LocalModelException("MODEL_UNKNOWN", "Không tìm thấy model local được yêu cầu.");

    private bool IsFileReady(LocalModelFile file, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != file.ExpectedSizeBytes)
        {
            _verifiedFiles.TryRemove(path, out _);
            return false;
        }

        if (file.ExpectedSha256 is null)
        {
            return true;
        }

        if (_verifiedFiles.TryGetValue(path, out var cached)
            && cached.Length == info.Length
            && cached.LastWriteAtUtc == info.LastWriteTimeUtc)
        {
            return cached.Valid;
        }

        try
        {
            using var stream = info.OpenRead();
            var actual = SHA256.HashData(stream);
            var valid = CryptographicOperations.FixedTimeEquals(
                actual,
                Convert.FromHexString(file.ExpectedSha256));
            _verifiedFiles[path] = new VerifiedModelFile(info.Length, info.LastWriteTimeUtc, valid);
            return valid;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void EnsureFreeSpace(long requiredBytes)
    {
        var root = Path.GetPathRoot(_paths.ModelsDirectory);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        const long safetyMargin = 512L * 1024 * 1024;
        if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes + safetyMargin)
        {
            throw new LocalModelException(
                "MODEL_DISK_SPACE_INSUFFICIENT",
                "Ổ đĩa không đủ dung lượng để tải model local.");
        }
    }

    private static void Report(
        IProgress<LocalModelDownloadProgress>? progress,
        string modelId,
        LocalModelFile file,
        long processedBefore,
        long fileProcessed,
        long totalBytes)
    {
        var processed = Math.Min(totalBytes, processedBefore + fileProcessed);
        progress?.Report(new LocalModelDownloadProgress(
            modelId,
            Path.GetFileName(file.RelativePath),
            processed,
            totalBytes,
            totalBytes <= 0 ? 0 : processed * 100d / totalBytes));
    }

    private static void DeletePartial(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        foreach (var gate in _downloadLocks.Values)
        {
            gate.Dispose();
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed record VerifiedModelFile(long Length, DateTime LastWriteAtUtc, bool Valid);
}
