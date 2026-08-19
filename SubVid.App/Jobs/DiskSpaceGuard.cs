namespace SubVid.App.Jobs;

internal static class DiskSpaceGuard
{
    public static void EnsureAvailable(string path, long requiredBytes, string operation)
    {
        if (requiredBytes <= 0)
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        try
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
            {
                throw new LocalJobException(
                    "DISK_SPACE_INSUFFICIENT",
                    $"Không đủ dung lượng trống để {operation}. Cần khoảng {FormatBytes(requiredBytes)}, "
                    + $"hiện còn {FormatBytes(drive.AvailableFreeSpace)} trên ổ {drive.Name}.",
                    retryable: false);
            }
        }
        catch (LocalJobException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Some network/removable volumes do not expose free-space information.
            // The operation can still proceed and surface its native I/O error.
        }
    }

    private static string FormatBytes(long bytes)
    {
        var gibibytes = bytes / (1024d * 1024d * 1024d);
        return gibibytes >= 1
            ? $"{gibibytes:0.0} GB"
            : $"{bytes / (1024d * 1024d):0} MB";
    }
}
