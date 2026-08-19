using BilibiliDownloader.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BilibiliDownloader.Infrastructure.Database;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<DownloadHistory> DownloadHistories => Set<DownloadHistory>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var history = modelBuilder.Entity<DownloadHistory>();
        history.ToTable("DownloadHistories");
        history.HasKey(item => item.Id);
        history.Property(item => item.VideoId).HasMaxLength(32).IsRequired();
        history.Property(item => item.SourceUrl).HasMaxLength(2048).IsRequired();
        history.Property(item => item.Title).HasMaxLength(500).IsRequired();
        history.Property(item => item.Quality).HasMaxLength(32).IsRequired();
        history.Property(item => item.Format).HasMaxLength(16).IsRequired();
        history.Property(item => item.FilePath).HasMaxLength(2048);
        history.Property(item => item.ErrorCode).HasMaxLength(64);
        history.Property(item => item.ErrorMessage).HasMaxLength(1000);
        history.HasIndex(item => item.CreatedAtUtc);
        history.HasIndex(item => item.VideoId);
        history.HasIndex(item => item.Status);

        var settings = modelBuilder.Entity<AppSettings>();
        settings.ToTable("AppSettings");
        settings.HasKey(item => item.Id);
        settings.Property(item => item.DownloadFolder).HasMaxLength(2048).IsRequired();
        settings.Property(item => item.FfmpegPath).HasMaxLength(2048);
        settings.Property(item => item.DefaultFormat).HasMaxLength(16).IsRequired();
    }
}
