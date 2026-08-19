namespace BilibiliDownloader.Infrastructure.Configuration;

public sealed class FFmpegOptions
{
    public const string SectionName = "FFmpeg";

    public string Version { get; set; } = "9.0.1";
    public string DownloadUrl { get; set; } =
        "https://github.com/GyanD/codexffmpeg/releases/download/9.0.1/ffmpeg-9.0.1-essentials_build.zip";
    public string Sha256 { get; set; } =
        "fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9";
    public string ArchiveRootDirectoryName { get; set; } = "ffmpeg-9.0.1-essentials_build";
    public string FfmpegRelativePath { get; set; } = "bin/ffmpeg.exe";
    public string FfprobeRelativePath { get; set; } = "bin/ffprobe.exe";
    public string[] AllowedHosts { get; set; } = ["github.com", "release-assets.githubusercontent.com"];
    public long MaximumDownloadBytes { get; set; } = 200L * 1024 * 1024;
    public long MaximumExtractedBytes { get; set; } = 750L * 1024 * 1024;
    public int DownloadTimeoutMinutes { get; set; } = 45;
    public int MaximumRetries { get; set; } = 2;
}
