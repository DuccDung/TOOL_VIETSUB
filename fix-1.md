# FIX-1 — Tạo giọng liên tục theo cụm thoại

## Mục tiêu

Các cue phụ đề liên tiếp thuộc cùng một mạch nói phải được đọc liên tục, không bị ngắt tại ranh giới giữa các cue.

Ví dụ:

- Cue 101: “Tôi muốn nói rằng”
- Cue 102: “chúng ta cần tiếp tục”
- Cue 103: “kế hoạch này ngay hôm nay”

Kết quả mong muốn là một đoạn giọng liền mạch:

> “Tôi muốn nói rằng chúng ta cần tiếp tục kế hoạch này ngay hôm nay.”

Phụ đề vẫn phải chuyển theo từng cue riêng lẻ trên timeline.

## Phân tích nguyên nhân hiện tại

- Một số cue đang được tổng hợp thành các file WAV riêng, sau đó ghép bằng `adelay`, nên có thể xuất hiện khoảng ngắt giữa các cue.
- `VoicePhrasePlanner` đã có cơ chế gom cue, nhưng cache `VOICE_CUE` cũ có thể khiến hệ thống bỏ qua việc tạo lại `VOICE_PHRASE`.
- Cách nối phrase hiện tại có thể tự chèn dấu chấm giữa các cue không có dấu câu, làm TTS tạo nhịp ngắt không mong muốn.
- Nếu đồng thời tồn tại audio phrase và audio từng cue, timeline có nguy cơ dùng sai loại audio hoặc bị chồng/lặp tiếng.

## TASK 1 — Xác định cue thuộc cùng một mạch nói

- [ ] Cập nhật quy tắc của `VoicePhrasePlanner`.
- [ ] Gom các cue khi cùng speaker.
- [ ] Gom các cue khi cùng voice ID.
- [ ] Gom các cue khi khoảng cách giữa chúng nằm trong giới hạn cho phép.
- [ ] Giới hạn tổng thời lượng phrase.
- [ ] Giới hạn tổng số ký tự phrase.
- [ ] Ưu tiên gom khi cue trước không kết thúc bằng `.`, `?`, `!`, `…`.
- [ ] Tách phrase khi cue trước là một câu hoàn chỉnh.
- [ ] Tách phrase khi khác speaker, khác voice hoặc có khoảng nghỉ thực sự dài.

File dự kiến:

- `SubVid.App/Jobs/VoicePhrasePlanner.cs`

## TASK 2 — Nối nội dung phrase đúng ngữ nghĩa

- [ ] Không tự chèn dấu chấm giữa các cue không có dấu câu.
- [ ] Cue không có dấu kết thúc câu được nối bằng một khoảng trắng.
- [ ] Giữ nguyên dấu phẩy, chấm phẩy và hai chấm nếu bản dịch đã có.
- [ ] Giữ nguyên `.`, `?`, `!`, `…` khi cue thực sự kết thúc câu.
- [ ] Không biến nội dung thành các câu rời như:

  `Tôi muốn nói rằng. chúng ta cần tiếp tục.`

- [ ] Đảm bảo ví dụ 101–103 tạo thành một chuỗi văn bản liên tục.

File dự kiến:

- `SubVid.App/Jobs/VoicePhrasePlanner.cs`

## TASK 3 — Ưu tiên audio phrase thay cho audio từng cue

- [ ] Khi phrase có nhiều cue và bật phrase synthesis, ưu tiên `VOICE_PHRASE`.
- [ ] Nếu phrase cache hợp lệ, dùng lại một file phrase duy nhất.
- [ ] Nếu phrase cache không tồn tại hoặc không hợp lệ, tạo lại toàn bộ phrase.
- [ ] Không để cache `VOICE_CUE` cũ ngăn việc tạo phrase.
- [ ] Xóa hoặc vô hiệu hóa các `VOICE_CUE` cũ thuộc phrase khi phrase mới đã sẵn sàng.
- [ ] Không dùng đồng thời `VOICE_PHRASE` và `VOICE_CUE` cho cùng một nhóm cue.
- [ ] Khi một cue trong phrase bị sửa, tạo lại toàn bộ phrase liên quan.
- [ ] Đảm bảo fingerprint của phrase thay đổi khi nội dung một cue thay đổi.

File dự kiến:

- `SubVid.App/Jobs/VoiceSynthesisJobExecutor.cs`

## TASK 4 — Giữ audio phrase liên tục trên timeline

- [ ] Đưa một audio input duy nhất vào timeline cho cả phrase.
- [ ] Bắt đầu audio tại thời điểm bắt đầu cue đầu tiên.
- [ ] Không chèn khoảng lặng riêng tại ranh giới cue con.
- [ ] Cho phép audio chạy qua các mốc cue 101, 102 và 103.
- [ ] Vẫn giữ thời gian riêng của từng cue để phụ đề hiển thị chính xác.
- [ ] Không tạo đồng thời phrase audio và các audio cue thành phần.
- [ ] Giữ cơ chế cảnh báo thời lượng nếu cả phrase vượt cửa sổ an toàn.

File dự kiến:

- `SubVid.App/Jobs/VoiceTimelineJobExecutor.cs`

## TASK 5 — Kiểm soát khoảng dừng tự nhiên

- [ ] Cue tiếp tục cùng câu: nối liền bằng khoảng trắng.
- [ ] Cue có dấu câu kết thúc: giữ nhịp nghỉ tự nhiên.
- [ ] Cue có dấu phẩy: giữ nhịp nghỉ ngắn.
- [ ] Khoảng cách timeline lớn: tách phrase.
- [ ] Khác speaker: luôn tách phrase.
- [ ] Không dùng một quy tắc nối phrase làm mất các khoảng nghỉ có chủ ý.

## TASK 6 — Cập nhật tùy chọn người dùng

- [ ] Đổi mô tả tùy chọn phrase thành nội dung dễ hiểu, ví dụ “Tạo giọng liền mạch theo cụm thoại”.
- [ ] Giải thích rằng các cue liên tiếp cùng người nói sẽ được tổng hợp thành một đoạn liên tục.
- [ ] Cho phép người dùng tạo lại phrase để chuyển project cũ đang dùng audio từng cue.
- [ ] Không bắt người dùng tự sửa từng subtitle chỉ để kích hoạt phrase synthesis.

Files dự kiến:

- `SubVid.App/ClientApp/src/components/VoiceSelectionDialog.tsx`
- `SubVid.App/ClientApp/src/components/VoiceWorkspace.tsx`

## TASK 7 — Hiển thị phrase trên giao diện

- [ ] Hiển thị cue đang thuộc cùng một phrase.
- [ ] Có thể hiển thị nhãn dạng `Cụm thoại 101–103`.
- [ ] Khi chọn một cue, có thể highlight các cue cùng phrase.
- [ ] Nếu phrase có cảnh báo thời lượng, hiển thị cảnh báo cho toàn phrase.
- [ ] Vẫn cho phép chỉnh sửa riêng từng cue.

Files dự kiến:

- `SubVid.App/ClientApp/src/components/SubtitlePanel.tsx`
- `SubVid.App/ClientApp/src/types.ts`
- `SubVid.App/Core/WorkspaceContracts.cs`
- `SubVid.App/Core/DesktopWorkspaceCoordinator.cs`

## TASK 8 — Kiểm thử

- [ ] Ba cue ví dụ 101–103 được gom thành một phrase.
- [ ] Nội dung phrase không có dấu chấm tự sinh giữa các cue.
- [ ] Phrase chỉ tạo một file audio.
- [ ] Project đã có `VOICE_CUE` cache vẫn được chuyển sang `VOICE_PHRASE`.
- [ ] Sửa một cue làm phrase liên quan được tạo lại.
- [ ] Khác speaker không bị gom.
- [ ] Dấu câu kết thúc câu làm phrase được tách đúng.
- [ ] Khoảng nghỉ dài làm phrase được tách đúng.
- [ ] Timeline chỉ dùng một input cho cả phrase.
- [ ] Không có khoảng ngắt nhân tạo tại ranh giới cue con.
- [ ] Cảnh báo thời lượng vẫn hoạt động trên phrase.
- [ ] Chạy test pipeline local và test xuất video.

Files test dự kiến:

- `SubVid.App.Tests/VoicePhrasePlannerTests.cs`
- `SubVid.App.Tests/VoiceSynthesisJobExecutorTests.cs`
- `SubVid.App.Tests/VoiceTimelineJobExecutorTests.cs`
- `SubVid.App.Tests/FullLocalPipelineIntegrationTests.cs`

## Tiêu chí nghiệm thu

Với ba cue:

- Cue 101: “Tôi muốn nói rằng”
- Cue 102: “chúng ta cần tiếp tục”
- Cue 103: “kế hoạch này ngay hôm nay”

Hệ thống phải:

1. Tạo một phrase duy nhất.
2. Gửi nội dung liền mạch cho TTS.
3. Không tự chèn dấu chấm giữa các cue.
4. Không có khoảng ngắt tại ranh giới 101–102 hoặc 102–103.
5. Phụ đề vẫn chuyển đúng theo từng cue.
6. Nếu phrase quá dài, vẫn tạo giọng và chỉ hiển thị cảnh báo.
7. Khi một cue được sửa, phrase liên quan được tạo lại.
8. Các câu khác nhau hoặc khác speaker vẫn được tách đúng.

## Phạm vi chưa thực hiện

- Chưa chỉnh sửa code.
- Chưa thay đổi cache hoặc dữ liệu project hiện có.
- Chưa chạy build hoặc test cho task này.
