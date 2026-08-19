namespace BilibiliDownloader.Domain.Enums;

public enum FFmpegProvisioningState
{
    Checking,
    Downloading,
    Verifying,
    Extracting,
    Validating,
    Ready,
    Failed,
    Cancelled
}
