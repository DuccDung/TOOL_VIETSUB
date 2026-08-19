using System.Net;
using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.Application.Services;
using BilibiliDownloader.Infrastructure.Bilibili;
using BilibiliDownloader.Infrastructure.Configuration;
using BilibiliDownloader.Infrastructure.Database;
using BilibiliDownloader.Infrastructure.Download;
using BilibiliDownloader.Infrastructure.FFmpeg;
using BilibiliDownloader.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BilibiliDownloader.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddBilibiliDownloaderInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<BilibiliOptions>(configuration.GetSection(BilibiliOptions.SectionName));
        services.Configure<DownloadOptions>(configuration.GetSection(DownloadOptions.SectionName));
        services.Configure<FFmpegOptions>(configuration.GetSection(FFmpegOptions.SectionName));

        services.AddSingleton<IFileService, FileStorageService>();
        services.AddDbContextFactory<AppDbContext>((serviceProvider, options) =>
        {
            var files = serviceProvider.GetRequiredService<IFileService>();
            var databasePath = Path.Combine(files.DataDirectory, "bilibili-downloader.db");
            options.UseSqlite($"Data Source={databasePath};Cache=Shared;Foreign Keys=True;Default Timeout=5");
        });

        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton<IBilibiliUrlParser, BilibiliUrlParser>();
        services.AddSingleton<IRemoteUriValidator, SafeRemoteUriValidator>();
        services.AddTransient<IBilibiliResolver, BilibiliResolver>();
        services.AddTransient<IBilibiliService, BilibiliService>();
        services.AddSingleton<IQualitySelectionService, QualitySelectionService>();
        services.AddSingleton<IFFmpegProcessRunner, FFmpegProcessRunner>();
        services.AddSingleton<IFFmpegEnvironment, FFmpegEnvironment>();
        services.AddSingleton<IFFmpegDiscoveryService, FFmpegDiscoveryService>();
        services.AddSingleton<IFFmpegPackageVerifier, FFmpegPackageVerifier>();
        services.AddSingleton<ISecureArchiveExtractor, SecureZipExtractor>();
        services.AddSingleton<IFFmpegProvisioningService, FFmpegProvisioningService>();
        services.AddSingleton<IFFmpegService, FFmpegService>();
        services.AddSingleton<IDownloadService, DownloadService>();

        services.AddHttpClient<BilibiliClient>((serviceProvider, client) =>
        {
            var options = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<BilibiliOptions>>()
                .Value;
            client.BaseAddress = new Uri(options.ApiBaseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
            ConfigureHeaders(client);
        }).ConfigurePrimaryHttpMessageHandler(CreateHandler);

        services.AddHttpClient<IThumbnailService, ThumbnailService>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            ConfigureHeaders(client);
        }).ConfigurePrimaryHttpMessageHandler(CreateHandler);

        services.AddHttpClient<IHttpDownloadClient, HttpDownloadClient>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            ConfigureHeaders(client);
        }).ConfigurePrimaryHttpMessageHandler(CreateHandler);

        services.AddHttpClient<IFFmpegPackageDownloader, FFmpegPackageDownloader>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BilibiliDownloader/1.0 FFmpegProvisioner");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/zip, application/octet-stream");
        }).ConfigurePrimaryHttpMessageHandler(CreateHandler);

        services.AddHostedService<ApplicationStartupService>();
        services.AddSingleton<DownloadManager>();
        services.AddSingleton<IDownloadManager>(serviceProvider => serviceProvider.GetRequiredService<DownloadManager>());
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<DownloadManager>());
        return services;
    }

    private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        MaxConnectionsPerServer = 8
    };

    private static void ConfigureHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) BilibiliDownloader/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
    }
}
