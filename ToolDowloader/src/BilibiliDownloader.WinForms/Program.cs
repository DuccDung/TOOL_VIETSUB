using BilibiliDownloader.Infrastructure;
using BilibiliDownloader.WinForms.Forms;
using BilibiliDownloader.WinForms.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using System.Globalization;

namespace BilibiliDownloader.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var logsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BilibiliDownloader",
            "Logs");
        Directory.CreateDirectory(logsDirectory);
        var logPath = Path.Combine(logsDirectory, $"app-{DateTime.Now:yyyy-MM-dd}.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                logPath,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14,
                shared: true,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Services.AddSerilog(Log.Logger, dispose: false);
            builder.Services.AddBilibiliDownloaderInfrastructure(builder.Configuration);
            builder.Services.AddSingleton<IWindowService, WindowService>();
            builder.Services.AddTransient<MainForm>();
            builder.Services.AddSingleton<GlobalExceptionHandler>();

            using var host = builder.Build();
            host.Start();
            using var globalHandler = host.Services.GetRequiredService<GlobalExceptionHandler>();
            globalHandler.Register();
            System.Windows.Forms.Application.Run(host.Services.GetRequiredService<MainForm>());

            using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            host.StopAsync(shutdownTimeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Application terminated unexpectedly");
            MessageBox.Show(
                $"Bilibili Downloader không thể khởi động.\n\n{exception.Message}\n\nLog: {logPath}",
                "Bilibili Downloader",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
