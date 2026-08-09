# TOOL_VIETSUB_APP - UI implementation checklist

Reference: desktop video editor layout supplied by the product owner.
Implementation: WinForms host + Microsoft Edge WebView2 + local React UI.

## Foundation

- [x] APP-UI-01 Define the dark blue design system and semantic tokens.
- [x] APP-UI-02 Add the WinForms WebView2 host and native window shell.
- [x] APP-UI-03 Add the local React, TypeScript, Tailwind, and icon toolchain.
- [x] APP-UI-04 Package frontend assets into the desktop application output.

## Main editor

- [x] APP-UI-05 Build the custom title bar and primary navigation.
- [x] APP-UI-06 Build the speech recognition and translation settings panel.
- [x] APP-UI-07 Build the central video preview and editing toolbar.
- [x] APP-UI-08 Build subtitle search, filters, list, and properties panel.
- [x] APP-UI-09 Build playback controls and the multitrack timeline.

## Interaction and quality

- [x] APP-UI-10 Add WebView2/native message bridge and local file picker.
- [x] APP-UI-11 Add empty, loaded, hover, focus, pressed, and disabled states.
- [x] APP-UI-12 Add keyboard navigation, accessible labels, and reduced motion.
- [x] APP-UI-13 Validate frontend production build and .NET solution build.
- [x] APP-UI-14 Run the application, capture screenshots, and visually refine it.
- [x] APP-UI-15 Add resizable editor panels with keyboard support and saved layout.

## V1 UI constraints

- The desktop app performs video work locally; it never connects to SQL Server.
- UI assets must run locally without an internet connection.
- The current milestone does not implement FFmpeg, transcription, translation,
  TTS, or final export processing.
- UI controls may use local mock state only to demonstrate interaction.
