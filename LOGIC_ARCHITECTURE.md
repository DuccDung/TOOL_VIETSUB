# TOOL_VIETSUB - Kiến trúc logic đã triển khai

Tài liệu này mô tả trạng thái thực tế của mã nguồn sau vòng triển khai nền tảng. Checklist kiểm duyệt nằm tại `LOGIC_IMPLEMENTATION_TASKS.md`.

## 1. Ranh giới hệ thống

```text
React UI trong WebView2
    -> message contract, không chứa token/connection string
WinForms TOOL_VIETSUB_APP
    -> HTTPS API, Bearer token
ASP.NET Core TOOL_VIETSUB_SERVER
    -> Entity Framework Core
SQL Server TOOL_VIETSUB
```

- Server quản lý tài khoản, phiên, dự án trung tâm, gói và quota.
- App quản lý file video, workspace, job và pipeline xử lý trên máy người dùng.
- App không kết nối SQL Server. Server không xử lý video nặng trong HTTP request.

## 2. Database và cấu hình Server

Triển khai SQL theo đúng thứ tự:

1. `TOOL_VIETSUB_V1.sql`
2. `TOOL_VIETSUB_AUTH_V2.sql`
3. `TOOL_VIETSUB_REGISTRATION_V3.sql`
4. `TOOL_VIETSUB_QUOTA_V4.sql`

V4 thêm `usage_reservations` để giữ trước hạn mức theo mô hình `HELD -> COMMITTED/RELEASED/EXPIRED`. Script có thể chạy lại an toàn và `schema_versions` chỉ có một bản ghi V4.

Không ghi JWT signing key, OTP secret hay mật khẩu SMTP vào source. Cấu hình Development bằng .NET User Secrets theo `TOOL_VIETSUB/AUTH_API.md`; Production dùng biến môi trường.

## 3. Phiên đăng nhập của App

- Access token chỉ tồn tại trong native host và tự refresh trước khi hết hạn.
- Refresh token được bảo vệ bằng Windows DPAPI, ràng buộc với user Windows hiện tại.
- Các API dự án/quota đi qua `AuthSessionManager.ExecuteAuthenticatedAsync`; semaphore ngăn nhiều luồng tự refresh cùng lúc.
- Nếu token bị thu hồi hoặc không hợp lệ, session cục bộ bị xóa và UI trở về đăng nhập.
- WebView không nhận access token, refresh token, đường dẫn file tuyệt đối hay chuỗi kết nối SQL.

## 4. Project workspace

Mặc định dữ liệu cục bộ nằm tại:

```text
%LocalAppData%\TOOL_VIETSUB\
  Projects\{project-guid}\
    project.json
    project.json.bak
    workspace.lock
    source\
    audio\
    subtitles\
    voice\
    cache\
    output\
    temp\
    logs\
  Tools\ffmpeg\
  Logs\
```

- `project.json` được lưu nguyên tử qua file tạm và backup.
- `LastCleanShutdown=false` cho biết phiên trước bị tắt đột ngột.
- `workspace.lock` chặn hai tiến trình sửa cùng một dự án.
- Mọi đường dẫn con đều được chuẩn hóa và kiểm tra không thoát khỏi workspace.
- Dự án local giữ cùng GUID với bản ghi dự án trên Server.

## 5. Nhập và bảo vệ video nguồn

- Hỗ trợ MP4, MKV, MOV và WEBM; giới hạn mặc định 50 GB và giới hạn thời lượng theo gói.
- FFprobe đọc metadata thật: duration, kích thước, FPS, codec, audio track, bitrate, rotation và VFR.
- `COPY` sao chép qua `.partial`, tính SHA-256, kiểm tra dung lượng đĩa rồi đặt file đích read-only.
- `LINK` giữ đường dẫn nguồn và tính SHA-256 nhưng không ghi vào file.
- Hủy import sẽ kill tiến trình/copy và dọn file tạm.
- Một dự án không được thay video nguồn sau khi nhập; tạo dự án mới để tránh dùng nhầm phụ đề/audio cũ.

FFmpeg được tìm theo thứ tự: biến môi trường `TOOL_VIETSUB_FFMPEG_PATH`/`TOOL_VIETSUB_FFPROBE_PATH`, thư mục `Tools\ffmpeg` trong AppData, thư mục `tools\ffmpeg` cạnh executable, sau đó PATH.

## 6. Job và quota

Job local được lưu trong manifest và có log JSONL riêng. Khi App khởi động lại, job đang `Running` trở thành `Interrupted`; người dùng có thể resume/retry từ checkpoint an toàn.

Tác vụ tính phí phải đi qua `QuotaProtectedJobService`:

```text
reserve quota -> persist reservation -> run local job
    -> Completed: commit số phút thực tế
    -> Failed/Cancelled: release
    -> Mất mạng: persist PENDING_* và reconcile khi mở dự án
```

Server dùng transaction `Serializable` cùng `UPDLOCK/HOLDLOCK` theo user, vì vậy hai máy không thể giữ vượt hạn mức. Request ID và reservation ID làm khóa idempotency.

## 7. Pipeline local hiện có

- `EXTRACT_AUDIO`: xác minh size/SHA-256 nguồn và tạo WAV mono 16 kHz bằng FFmpeg.
- `TRANSCRIBE_LOCAL`: Whisper Base multilingual trên CPU, auto-detect ngôn ngữ,
  timestamp, checkpoint và bảo vệ cue khóa.
- `OCR_LOCAL`: FFmpeg trích vùng frame, PaddleOCR English V5 CPU và gộp frame
  thành cue theo timeline.
- `TRANSLATE_LOCAL`: Argos worker process cô lập, batch English -> Vietnamese,
  checkpoint và không ghi đè bản dịch khóa.
- `SYNTHESIZE_VOICE_LOCAL`: Piper worker process cô lập, voice VAIS-1000,
  cache theo nội dung/model/voice và WAV theo từng cue.
- `EXPORT_VIDEO_LOCAL`: fit voice cue bằng atempo/pad/trim, tạo voice timeline,
  sidechain ducking, mix/limiter, H.264/AAC MP4 và soft subtitle `mov_text`.
- Mọi output quan trọng dùng file `.partial` rồi move nguyên tử; source video
  bị chặn ghi đè và được kiểm tra SHA-256 trước OCR/STT/export.
- WebView phát video thật qua virtual HTTPS Range handler. Timeline chọn/seek,
  tách, căn, nhân bản và xóa cue; thay đổi được lưu vào manifest.

Model/runtime được tự chuẩn bị tại máy người dùng bằng registry có checksum.
App chỉ gửi metadata dự án và usage/quota lên Server, không gửi media hay nội
dung phụ đề. Giới hạn V1: dịch English -> Vietnamese, một speaker/một voice,
không lip-sync, voice cloning hay batch processing.

## 8. API nghiệp vụ mới

| Method | Endpoint | Mục đích |
| --- | --- | --- |
| POST | `/api/v1/projects` | Tạo/idempotently đồng bộ dự án |
| GET | `/api/v1/projects` | Liệt kê tối đa 100 dự án của user |
| GET | `/api/v1/projects/{id}` | Lấy dự án thuộc user |
| PATCH | `/api/v1/projects/{id}/name` | Đổi tên dự án |
| POST | `/api/v1/usage/reservations` | Giữ trước quota |
| POST | `/api/v1/usage/reservations/{id}/commit` | Ghi nhận số phút thực tế |
| POST | `/api/v1/usage/reservations/{id}/release` | Trả lại quota |

Tất cả endpoint trên yêu cầu Bearer token và kiểm tra ownership theo user hiện tại.

## 9. Kiểm thử

Chạy toàn bộ:

```powershell
dotnet test TOOL_VIETSUB.slnx -c Release
dotnet build TOOL_VIETSUB.slnx -c Release
```

Hiện có 63 test. Khi cấu hình runtime/model thật, toàn bộ suite chạy không skip
và bao phủ cả chuỗi MP4 -> Whisper -> Argos -> Piper -> đồng bộ/mix -> MP4,
video OCR thật, FFprobe/FFmpeg thật, source byte-for-byte không đổi, model hash,
timeline persistence, job recovery và quota settlement.
