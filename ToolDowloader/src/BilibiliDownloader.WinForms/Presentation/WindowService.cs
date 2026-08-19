using BilibiliDownloader.Application.Interfaces;
using BilibiliDownloader.WinForms.Forms;
using Microsoft.Extensions.Logging;

namespace BilibiliDownloader.WinForms.Presentation;

public sealed class WindowService(
    ISettingsService settingsService,
    IFFmpegService ffmpegService,
    IFFmpegProvisioningService ffmpegProvisioningService,
    IHistoryService historyService,
    IFileService fileService,
    IBilibiliService bilibiliService,
    IDownloadManager downloadManager,
    IQualitySelectionService qualitySelectionService,
    ILoggerFactory loggerFactory) : IWindowService
{
    public Task ShowSettingsAsync(IWin32Window owner)
    {
        using var form = new SettingsForm(
            settingsService,
            ffmpegService,
            ffmpegProvisioningService,
            loggerFactory.CreateLogger<SettingsForm>());
        form.ShowDialog(owner);
        return Task.CompletedTask;
    }

    public Task ShowHistoryAsync(IWin32Window owner)
    {
        using var form = new HistoryForm(
            historyService,
            fileService,
            bilibiliService,
            downloadManager,
            qualitySelectionService,
            settingsService,
            loggerFactory.CreateLogger<HistoryForm>());
        form.ShowDialog(owner);
        return Task.CompletedTask;
    }

    public void ShowAbout(IWin32Window owner)
    {
        using var form = new AboutForm();
        form.ShowDialog(owner);
    }
}
