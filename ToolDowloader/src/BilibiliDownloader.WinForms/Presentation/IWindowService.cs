namespace BilibiliDownloader.WinForms.Presentation;

public interface IWindowService
{
    Task ShowSettingsAsync(IWin32Window owner);
    Task ShowHistoryAsync(IWin32Window owner);
    void ShowAbout(IWin32Window owner);
}
