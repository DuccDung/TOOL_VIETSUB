# Bilibili Downloader

Local Windows desktop application for analyzing and downloading Bilibili videos that the user is permitted to access.

The application does not bypass DRM, CAPTCHA, paywalls, authentication, regional restrictions, or other access controls.

## Development

Requirements: Windows and the .NET 9 SDK. FFmpeg is discovered automatically and, when absent,
the application downloads a pinned portable build into `%LocalAppData%\BilibiliDownloader\Tools`.

```powershell
dotnet restore BilibiliDownloader.sln
dotnet build BilibiliDownloader.sln -c Release
dotnet test BilibiliDownloader.sln -c Release
```

## Publish Windows x64

```powershell
.\build\publish-win-x64.ps1
```

The self-contained output is written to `artifacts\publish\win-x64`. At runtime the application checks,
in order, the custom Settings path, bundled tools, its managed LocalAppData installation, and the Windows
`PATH`. If no valid executable is found, it downloads the pinned package over HTTPS, verifies SHA-256,
extracts it safely, validates `ffmpeg -version`, and stores the resolved path. A manual path remains
available in Settings.

To bundle FFmpeg instead, place a properly licensed `ffmpeg.exe` and `ffprobe.exe` in
`src\BilibiliDownloader.WinForms\Tools\ffmpeg` before publishing.

Compile `installer\BilibiliDownloader.iss` with Inno Setup after publishing to create the optional per-user installer.
