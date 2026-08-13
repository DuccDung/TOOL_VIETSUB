# Local AI runtime and model setup

The App processes video, audio, transcript, translation and voice files on the
user's machine. The Server receives authentication, project metadata and quota
usage only; it does not receive media content or subtitle text from this local
pipeline.

## Automatic first-run setup

The first action that needs a model downloads only the required component:

- Whisper Base multilingual: about 148 MB.
- OPUS-MT Chinese to Vietnamese (CTranslate2 INT8): about 82 MB.
- Argos English to Vietnamese: about 68 MB.
- Piper `vi_VN-vais1000-medium`: about 63 MB.
- Python 3.11, CTranslate2, SentencePiece, Argos and Piper are installed into an isolated App tools
  directory through pinned `uv` 0.12.3.

Every registered model has a pinned size and SHA-256. Downloads use HTTPS, a
host allow-list and `.partial` files; a verified file is moved atomically into
place. Existing files are re-hashed when their size or modification time
changes. The uv archive and executable are also verified against pinned
SHA-256 values.

Default locations:

```text
%LocalAppData%\TOOL_VIETSUB\Models\
%LocalAppData%\TOOL_VIETSUB\Tools\python\
%LocalAppData%\TOOL_VIETSUB\Tools\ffmpeg\
```

Developer/test overrides:

```text
TOOL_VIETSUB_MODEL_ROOT
TOOL_VIETSUB_PYTHON_PATH
TOOL_VIETSUB_FFMPEG_PATH
TOOL_VIETSUB_FFPROBE_PATH
```

## V1 capability boundary

- STT: multilingual automatic detection or an explicit Chinese/English hint through Whisper Base.
- OCR: PaddleOCR Chinese V5 or English V5, optimized for hard subtitles in the lower part of
  the video; crop ratio and sampling interval are stored in project settings.
- Translation: direct Chinese to Vietnamese through pinned OPUS-MT/CTranslate2,
  plus English to Vietnamese through the pinned Argos package. Other source
  languages are rejected explicitly instead of silently producing an incorrect translation.
- TTS: one Vietnamese `vais1000` voice on CPU.
- Export: H.264/AAC MP4, source-audio ducking, mixed Vietnamese voice and a
  Vietnamese `mov_text` subtitle stream. The source video is never overwritten.
- V1 is designed for one speaker and projects of roughly 10-20 minutes. It does
  not provide voice cloning, lip sync or batch processing.

## Verification commands

Normal deterministic suite:

```powershell
dotnet test TOOL_VIETSUB_APP.Tests\TOOL_VIETSUB_APP.Tests.csproj -c Release
dotnet build TOOL_VIETSUB.slnx -c Release
```

Installed-runtime integration tests additionally use the four developer/test
overrides above and `TOOL_VIETSUB_TEST_SPEECH_WAV`. They execute Whisper,
PaddleOCR, Argos, Piper and FFmpeg on real media and assert that the original
source bytes remain unchanged.
