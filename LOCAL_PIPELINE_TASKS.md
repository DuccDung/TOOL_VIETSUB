# TOOL_VIETSUB - Local pipeline implementation

Chỉ đánh dấu `[x]` sau khi mã nguồn đã build và bài kiểm thử tương ứng đã vượt qua.

## Gate 0 - Local runtime

- [x] Kiểm kê máy phát triển: i7-11800H, RAM 16 GB, RTX 3050 4 GB.
- [x] Media/text inference chạy trên App; Server chỉ nhận metadata và mức sử dụng.
- [x] Whisper.net/whisper.cpp CPU, PaddleOCR V5 CPU, Argos và Piper local.
- [x] Model registry có phiên bản, dung lượng, SHA-256, tải `.partial` và đặt file nguyên tử.
- [x] Tự cài Python 3.11 cô lập qua uv đã ghim phiên bản/checksum.
- [x] Có inventory license và attribution tại `THIRD_PARTY_NOTICES.md`.

## Gate 1 - Playback và timeline

- [x] Stream đúng video hiện tại qua HTTPS virtual URL, hỗ trợ HTTP Range.
- [x] Chặn traversal và không lộ đường dẫn local cho WebView.
- [x] Phát, pause, seek, tốc độ, âm lượng và duration thật.
- [x] Đồng bộ playhead/cue; click cue để chọn và seek; zoom và phím tắt.
- [x] Cắt cue tại playhead, căn đầu cue, nhân bản, xóa và bookmark trên timeline.
- [x] Thay đổi cue được lưu vào manifest; nội dung người dùng được khóa và audio TTS cũ bị vô hiệu.
- [x] Test Range, Unicode, timeline validation và persistence.

## Gate 2 - STT local

- [x] Whisper.net 1.9.1 CPU và model Base multilingual.
- [x] Auto-detect ngôn ngữ, transcript có timestamp.
- [x] Job `TRANSCRIBE_LOCAL`: extract audio, checkpoint, cue lock, cancel/retry/resume.
- [x] Nút Nhận dạng, tiến trình tải model và tiến trình job đã nối với native host.
- [x] Test WAV thật với model thật.

## Gate 3 - OCR local

- [x] PaddleOCR V5 CPU nhận vùng frame đã crop.
- [x] FFmpeg trích frame theo interval, dọn toàn bộ file tạm khi xong/lỗi/hủy.
- [x] Gộp frame giống nhau thành cue và bỏ kết quả confidence thấp.
- [x] Job `OCR_LOCAL`, checkpoint, cue lock, tiến trình và retry/resume.
- [x] Xác minh SHA-256 video nguồn trước OCR.
- [x] Test ảnh thật và video phụ đề cứng thật.

## Gate 4 - Dịch và biên tập

- [x] Argos worker chạy process local cô lập, I/O UTF-8 trên Windows.
- [x] Gói English -> Vietnamese được ghim dung lượng/SHA-256 và cài offline.
- [x] Dịch batch, checkpoint từng batch, bỏ qua cue dịch đã khóa.
- [x] Kiểm tra số lượng/chuỗi rỗng và từ chối cặp ngôn ngữ chưa hỗ trợ.
- [x] Nút Dịch thiếu, sửa transcript/bản dịch và export SRT đã nối thật.
- [x] Test Unicode, batch, locked cue, worker và model thật.
- [ ] Glossary và mô hình dịch đa ngôn ngữ/pivot ngoài English -> Vietnamese thuộc bản sau V1.

## Gate 5 - TTS và đồng bộ

- [x] Piper 1.6 chạy process local cô lập với voice `vi_VN-vais1000-medium`.
- [x] Cache theo hash text/model/voice; hash WAV được kiểm tra trước khi tái sử dụng.
- [x] WAV có metadata duration/sample rate/channel và liên kết đúng cue.
- [x] Fit duration bằng chuỗi `atempo`, pad/trim và đặt đúng delay timeline.
- [x] Tạo voice timeline stereo 48 kHz, peak limiter và dọn file tạm.
- [x] Test cache, WAV invalid, atempo, Unicode và Piper thật.

## Gate 6 - Mix và export

- [x] Duck audio gốc bằng sidechain, mix giọng Việt và peak limiter.
- [x] Export H.264/AAC MP4 qua file `.partial`; chặn mọi đường dẫn ghi đè nguồn.
- [x] Nhúng soft subtitle `mov_text` tiếng Việt và xuất SRT riêng.
- [x] FFprobe xác minh video/audio/duration đầu ra trước khi công bố file.
- [x] Xác minh SHA-256 source, voice cue và voice timeline trước khi dùng.
- [x] Test export FFmpeg thật và xác nhận video nguồn không đổi một byte.

## Gate 7 - Job, quota và end-to-end

- [x] Mỗi project chỉ có một job Pending/Running/Paused tại một thời điểm.
- [x] Reserve quota trước job; Completed commit, Failed/Cancelled release.
- [x] Retry sau release tạo reservation/job mới; resume dùng hold còn hạn.
- [x] PENDING settlement được lưu và reconcile khi mở lại project.
- [x] Tự tải lại dependency bị thiếu trước resume/retry.
- [x] UI hiển thị và điều khiển job mới nhất của toàn pipeline, gồm retry/cancel.
- [x] Test end-to-end thật: MP4 -> Whisper -> Argos -> Piper -> sync -> MP4.
- [x] Toàn bộ 63 test vượt qua với runtime/model thật, không skip.
- [ ] Đóng gói installer và kiểm thử ma trận nhiều máy/codec là gate phát hành riêng.
