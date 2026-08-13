# TOOL_VIETSUB - Checklist triển khai logic

Chỉ đánh dấu `[x]` khi mã nguồn đã build và bài kiểm thử tương ứng đã vượt qua.

## Gate 0 - Baseline

- [x] Đối chiếu kiến trúc Server/App với `AI.md` và tài liệu nghiệp vụ Word.
- [x] Xác nhận database có schema V1, V2, V3 và V4.
- [x] Build toàn bộ Solution ở cấu hình Release: 0 warning, 0 error.
- [x] Có project test tự động cho các lớp logic mới.

## Gate 1 - Phiên đăng nhập

- [x] Refresh token được lưu bằng DPAPI theo tài khoản Windows.
- [x] Tự khôi phục và refresh phiên khi mở App.
- [x] Server kiểm tra session/tài khoản trên mỗi access token.
- [x] Đưa về trạng thái chưa đăng nhập khi phiên hết hạn hoặc bị thu hồi.
- [x] Lấy tài khoản, quyền, gói và lịch sử sử dụng sau đăng nhập.
- [x] Mọi API nghiệp vụ của App đi qua một cổng xác thực có refresh tuần tự.
- [x] Tác vụ tính phí phải reserve quota trước khi job được tạo/chạy.
- [ ] Bổ sung contract test HTTP cho nhiều request đồng thời đúng lúc access token hết hạn.

## Gate 2 - Project Workspace

- [x] Tạo, mở, đổi tên và liệt kê dự án gần đây.
- [x] Đồng bộ định danh/tên dự án lên Server qua API có xác thực.
- [x] Manifest có phiên bản schema và cấu trúc media/subtitle/audio/settings/job.
- [x] Autosave nguyên tử, có backup và phục hồi manifest lỗi.
- [x] Đánh dấu phiên làm việc bị đóng bất thường và khóa mở trùng workspace.
- [x] Chặn path traversal ra ngoài workspace.
- [x] Không ghi trực tiếp lên video nguồn; bản COPY được đặt read-only.
- [x] Test đường dẫn Unicode, backup lỗi, autosave và khóa phiên.

## Gate 3 - Media Import

- [x] Kiểm tra định dạng, dung lượng và thời lượng theo gói.
- [x] Đọc duration, resolution, FPS, codec, audio track và rotation bằng FFprobe.
- [x] Phát hiện file hỏng, thiếu luồng video hoặc thiếu audio.
- [x] Hỗ trợ COPY và LINK.
- [x] Có tiến trình, tốc độ, hủy và dọn file `.partial`.
- [x] Xác minh SHA-256 và không thay đổi video nguồn.
- [x] Không cho thay video nguồn của cùng dự án để tránh lệch phụ đề/audio cũ.
- [x] Test bằng video H.264/AAC thật được tạo bởi FFmpeg.

## Gate 4 - Job Manager

- [x] State machine Pending/Running/Paused/Interrupted/Completed/Failed/Cancelled.
- [x] Chống chạy trùng và giới hạn số job chạy đồng thời.
- [x] Chạy nền, không chặn UI.
- [x] Pause/resume/cancel/retry tại checkpoint an toàn.
- [x] Lưu log JSONL kỹ thuật và lỗi thân thiện trong manifest.
- [x] Khôi phục job đang chạy thành `Interrupted` sau sự cố.
- [x] Test trạng thái sai, chạy trùng, pause/resume, cancel, retry và recovery.

## Gate 5 - Quota

- [x] API reserve/commit/release idempotent.
- [x] Khóa giao dịch theo người dùng để chống giữ vượt hạn mức.
- [x] Kiểm tra feature, thời lượng tối đa và phút còn lại trước khi reserve.
- [x] Reservation hết hạn được giải phóng tự động.
- [x] Chỉ commit phút thực tế khi thành công; release khi failed/cancelled.
- [x] Lưu `PENDING_COMMIT`/`PENDING_RELEASE` và đồng bộ lại khi mở dự án.
- [x] Test gửi lặp, xử lý song song, hết hạn và mất mạng lúc quyết toán.

## Gate 6 - Pipeline

- [x] Trích xuất và chuẩn hóa audio WAV mono PCM 16 kHz bằng FFmpeg.
- [x] Import, sửa và export phụ đề SRT Unicode theo từng cue.
- [x] Whisper local nhận dạng giọng nói và checkpoint transcript.
- [x] PaddleOCR local nhận dạng phụ đề cứng từ video.
- [x] Biên tập timeline và dịch English -> Vietnamese bằng Argos local.
- [x] Piper local tạo giọng Việt và cache theo từng cue.
- [x] Đồng bộ audio theo timeline bằng atempo/pad/trim/delay.
- [x] Sidechain ducking, trộn/xuất MP4 và FFprobe kiểm tra file đầu ra.
- [x] Model/runtime registry có checksum, tải nguyên tử và tự cài runtime cô lập.

## Gate 7 - Release

- [x] 63 unit/integration/contract/recovery/end-to-end test đều vượt qua,
  gồm test runtime/model/media thật không skip.
- [x] Build Release: 0 warning, 0 error.
- [x] `npm audit` và NuGet vulnerability scan: không phát hiện lỗ hổng đã biết.
- [x] Secret không nằm trong source; SMTP/JWT/OTP dùng User Secrets hoặc biến môi trường.
- [x] Dữ liệu do integration test tạo đã được dọn khỏi SQL Server.
- [ ] Test bộ cài/publish trên máy Windows sạch có WebView2 và FFmpeg.
- [x] Kiểm thử end-to-end MP4 -> Whisper -> Argos -> Piper -> MP4 và xác nhận
  source không đổi một byte.
