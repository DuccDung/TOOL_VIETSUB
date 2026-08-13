# TOOL_VIETSUB - Ghi chú triển khai cho AI

## 1. Nguồn yêu cầu nghiệp vụ chính

Trước khi phân tích hoặc triển khai tính năng, phải đọc tài liệu:

`D:\laptrinhweb\code_outsrc\TOOL_VIETSUB\Ke_hoach_trien_khai_he_thong_dich_video_tieng_Viet.docx`

Tài liệu này mô tả nghiệp vụ chuyển video tiếng nước ngoài thành video lồng
tiếng Việt, bao gồm transcript, bản dịch, giọng đọc, đồng bộ thời gian, phụ đề
và video đầu ra.

Nếu kiến trúc trong tài liệu Word khác với yêu cầu tại file này về vị trí xử
lý video, phải ưu tiên yêu cầu trong `AI.md`: video được xử lý trên ứng dụng
WinForms.

## 2. Kiến trúc sản phẩm bắt buộc

Solution có hai ứng dụng với trách nhiệm tách biệt:

### TOOL_VIETSUB_SERVER

- Là ứng dụng Web ASP.NET Core và API trung tâm.
- Quản lý người dùng, đăng nhập, phân quyền, trạng thái tài khoản và quota.
- Quản lý dữ liệu dự án, lịch sử tác vụ, trạng thái, mức sử dụng và chi phí khi
  các tính năng này được triển khai.
- Là thành phần duy nhất được phép kết nối trực tiếp tới SQL Server.
- Sử dụng database `TOOL_VIETSUB` và Entity Framework Core.
- Không thực hiện các tác vụ xử lý video nặng bên trong HTTP request.

### TOOL_VIETSUB_APP

- Là ứng dụng desktop WinForms cài trên máy người dùng.
- Là nơi trực tiếp xử lý video và lưu các tệp làm việc cục bộ.
- Thực hiện pipeline FFmpeg, tách âm thanh, nhận diện lời nói, dịch, tạo giọng
  Việt, đồng bộ thời gian, trộn âm thanh và xuất kết quả.
- Hiển thị giao diện để người dùng kiểm tra, sửa transcript và bản dịch trước
  khi tạo giọng.
- Đăng nhập và trao đổi dữ liệu với Server qua HTTPS API.
- Không được kết nối hoặc truy vấn SQL Server trực tiếp.
- Không tham chiếu trực tiếp các entity EF Core của Server; giao tiếp bằng API
  contract/DTO.

Luồng kết nối tổng quát:

`TOOL_VIETSUB_APP -> HTTPS API -> TOOL_VIETSUB_SERVER -> SQL Server`

Luồng dữ liệu chi tiết giữa App và Server phải được thực hiện qua API có xác
thực. Không đưa thông tin kết nối SQL Server vào ứng dụng WinForms.

## 3. Nghiệp vụ xử lý video chính

1. Người dùng đăng nhập trên WinForms App.
2. Người dùng chọn video nguồn trên máy.
3. App kiểm tra định dạng, dung lượng và thời lượng video.
4. App dùng FFmpeg tách và chuẩn hóa âm thanh.
5. App nhận diện lời nói, tạo transcript kèm timestamp.
6. App dịch từng đoạn sang tiếng Việt có xét ngữ cảnh và glossary.
7. Người dùng kiểm tra, chỉnh sửa và duyệt transcript/bản dịch.
8. App tạo giọng đọc tiếng Việt cho các đoạn đã duyệt.
9. App điều chỉnh tốc độ, khoảng nghỉ và thời lượng để bám timestamp gốc.
10. App trộn giọng Việt với nhạc nền và xuất video MP4, phụ đề SRT cùng
    transcript.
11. App đồng bộ trạng thái, lịch sử và mức sử dụng cần thiết lên Server qua API.

## 4. Phạm vi V1/MVP

- Ưu tiên đầu vào MP4, thời lượng tối đa khoảng 10-20 phút.
- Tự nhận diện ngôn ngữ nguồn; ngôn ngữ đầu ra là tiếng Việt.
- Một người nói và một giọng Việt có sẵn.
- Cho phép sửa transcript và bản dịch theo từng câu trước khi tạo giọng.
- Kết quả gồm video MP4, phụ đề SRT và transcript.
- Từng bước xử lý cần có trạng thái, log, checkpoint và khả năng chạy lại.
- Chưa triển khai lip-sync, voice cloning, nhiều speaker, batch processing hoặc
  studio chỉnh sửa nâng cao.

## 5. Dữ liệu trung tâm của một đoạn thoại

Mỗi đoạn thoại tối thiểu cần có:

- Thời gian bắt đầu và kết thúc.
- Người nói.
- Nội dung gốc.
- Nội dung dịch tiếng Việt.
- Giọng Việt được chọn.
- Trạng thái biên tập/phê duyệt.
- Thông tin audio TTS và tốc độ đọc khi đã tạo giọng.

Các câu đã được người dùng duyệt hoặc khóa không được tự ý thay đổi khi chạy
lại pipeline.

## 6. Nguyên tắc triển khai

- Giữ ranh giới rõ ràng giữa Server và WinForms App.
- Mọi truy cập dữ liệu tập trung phải đi qua API của Server.
- Không lưu API key, mật khẩu hoặc secret trong source code hay database dưới
  dạng văn bản thuần.
- Không gửi toàn bộ video qua Server nếu tính năng đang được thiết kế để xử lý
  cục bộ và chưa có yêu cầu lưu trữ video tập trung.
- Các tác vụ dài trên WinForms phải chạy nền, hỗ trợ hủy, báo tiến độ và không
  làm treo giao diện.
- FFmpeg và tệp tạm phải được quản lý rõ ràng; luôn dọn tệp tạm an toàn sau khi
  hoàn tất hoặc thất bại.
- Trước khi thêm tính năng ngoài MVP, phải đối chiếu tài liệu nghiệp vụ và xác
  nhận phạm vi với người dùng.

## 7. Hiện trạng liên quan

- Project Web: `TOOL_VIETSUB/TOOL_VIETSUB_SERVER.csproj`.
- Project WinForms: `TOOL_VIETSUB_APP/TOOL_VIETSUB_APP.csproj`.
- Database schema V1: `TOOL_VIETSUB_V1.sql`.
- Authentication schema V2: `TOOL_VIETSUB_AUTH_V2.sql`.
- Registration schema V3: `TOOL_VIETSUB_REGISTRATION_V3.sql`.
- Quota reservation schema V4: `TOOL_VIETSUB_QUOTA_V4.sql`.
- EF Core DbContext của Server: `TOOL_VIETSUB.Data.ToolVietSubDbContext`.
- Kiến trúc logic thực tế: `LOGIC_ARCHITECTURE.md`.
- Trạng thái kiểm duyệt từng gate: `LOGIC_IMPLEMENTATION_TASKS.md`.
- Nền tảng project/media/job/quota, playback, timeline, trích xuất audio và SRT đã được triển khai.
- Pipeline V1 đã chốt phương pháp local, không dùng API key AI: Whisper.net,
  PaddleOCR, Argos Translate và Piper. Không thay provider/model hoặc chuyển nội
  dung media lên cloud nếu chưa được người dùng xác nhận.
- Đã triển khai STT, OCR phụ đề cứng, dịch English -> Vietnamese, giọng Việt,
  fit timeline, duck/mix audio và xuất MP4/SRT. Xem trạng thái đã kiểm thử tại
  `LOCAL_PIPELINE_TASKS.md`, cách cài tại `LOCAL_AI_SETUP.md` và license tại
  `THIRD_PARTY_NOTICES.md`.
- Các model/runtime phải tiếp tục đi qua registry/download có checksum; không
  thêm URL tải tùy ý hoặc thực thi binary chưa xác minh.
