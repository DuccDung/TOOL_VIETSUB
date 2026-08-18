using System.Text.Json;

namespace SubVid.App.Tests;

internal static class TestDatabase
{
    public static string ConnectionString { get; } = ResolveConnectionString();

    private static string ResolveConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable("SUBVID_TEST_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var path = Path.Combine(directory.FullName, "SubVid.Server", "appsettings.json");
                if (File.Exists(path))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (document.RootElement.TryGetProperty("ConnectionStrings", out var connections)
                        && connections.TryGetProperty("SubVidDatabase", out var value)
                        && !string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        return value.GetString()!;
                    }
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException(
            "Set SUBVID_TEST_CONNECTION_STRING or provide SubVid.Server/appsettings.json.");
    }
}
