# Nâng cấp dịch phụ đề có ngữ cảnh

## Kết quả triển khai

Ứng dụng hỗ trợ năm lựa chọn dịch:

- `local`: Argos cho Anh → Việt và OPUS-MT cho Trung → Việt.
- `openai`: gọi Responses API và yêu cầu Structured Outputs.
- `gemini`: gọi Gemini `generateContent` với JSON Schema.
- `deepseek`: gọi Chat Completions API, dùng JSON Output và tắt thinking để giữ độ trễ/chi phí ổn định.
- `groq`: gọi OpenAI-compatible Chat Completions; GPT-OSS dùng JSON Schema strict, model tùy chỉnh dùng JSON Object Mode.

Tên model không bị gắn cứng vào pipeline. Giao diện cung cấp giá trị mặc định nhưng cho phép thay model khi nhà cung cấp thay đổi danh mục.

Tài liệu API đối chiếu khi triển khai:

- OpenAI Structured Outputs: <https://developers.openai.com/api/docs/guides/structured-outputs>
- Gemini Structured Outputs: <https://ai.google.dev/gemini-api/docs/structured-output>
- DeepSeek Chat Completions: <https://api-docs.deepseek.com/api/create-chat-completion>
- DeepSeek JSON Output: <https://api-docs.deepseek.com/guides/json_mode>
- Groq OpenAI compatibility: <https://console.groq.com/docs/openai>
- Groq Structured Outputs: <https://console.groq.com/docs/structured-outputs>
- Groq Free Tier rate limits: <https://console.groq.com/docs/rate-limits>

## Luồng dữ liệu

1. Cue được chia theo khoảng ngắt giữa các cảnh.
2. Mỗi nhóm tối đa 12 cue cần dịch nhận thêm ba cue trước/sau làm ngữ cảnh; Groq giới hạn tối đa 8 target cue để phù hợp Free Tier.
3. Request chứa cue ID, timestamp, speaker, nội dung nguồn và giới hạn ký tự gợi ý.
4. Cloud nhận thêm tóm tắt dự án, quy tắc xưng hô, phong cách, glossary và các bản dịch thủ công đã duyệt.
5. Kết quả phải trả đúng một bản dịch cho từng cue ID và đúng thứ tự. Thiếu, thừa hoặc trùng ID làm cả cảnh bị từ chối.
6. Nếu bật kiểm duyệt, cloud thực hiện lượt thứ hai để sửa sai nghĩa, xưng hô, thuật ngữ, bỏ sót và độ dài.
7. Kết quả được kiểm tra số liệu, glossary, tốc độ đọc, độ tin cậy và lỗi lặp trước khi lưu.

Cue đã khóa không bị dịch lại. Khi context, glossary, model hoặc câu lân cận thay đổi, fingerprint thay đổi và chỉ các cue chưa khóa liên quan được xử lý lại.

## Cấu hình trong ứng dụng

Trong mục **Dịch sang tiếng Việt**:

1. Chọn Local, OpenAI, Gemini, DeepSeek hoặc Groq.
2. Chọn chế độ chất lượng và model.
3. Với cloud, nhập API key rồi bấm **Lưu cấu hình**.
4. Nhập tóm tắt video, nhân vật/cách xưng hô và phong cách dịch.
5. Nhập glossary, mỗi dòng theo dạng:

   `từ gốc = tiếng Việt | ghi chú tùy chọn`

6. Bật lượt kiểm duyệt nếu ưu tiên chất lượng; bật fallback nếu muốn chuyển sang local khi cloud lỗi tạm thời.

API key được mã hóa bằng Windows DPAPI theo tài khoản hiện tại và lưu ngoài project. `project.json`, cache, job parameters và log không chứa key. Chỉ văn bản cần dịch cùng ngữ cảnh cấu hình được gửi tới nhà cung cấp; video và audio không được gửi.

Với Groq, ứng dụng tôn trọng `Retry-After` khi gặp giới hạn tốc độ. Free Tier có quota theo phút/ngày và không phải gói gọi API không giới hạn. Có thể bật Zero Data Retention trong Groq Console cho nội dung nhạy cảm.

## Translation Memory

Khi người dùng sửa và lưu một bản dịch thủ công, cặp câu nguồn/đích được cập nhật vào Translation Memory của project. Pipeline chọn tối đa 20 ví dụ liên quan cho mỗi cảnh. Bộ nhớ được giới hạn ở 500 mục gần nhất.

## Trạng thái chất lượng

- `VALID`: qua kiểm tra tự động và không có cảnh báo.
- `REVIEW`: bản dịch dùng được nhưng có cảnh báo như độ tin cậy thấp, số liệu khác nguồn, thiếu glossary hoặc tốc độ đọc cao.
- `INVALID`: đầu ra rỗng, lặp bệnh lý, quá dài bất thường hoặc sai cấu trúc; dữ liệu cũ được giữ nguyên.

## Việc cần làm trước khi phát hành rộng

- Chuẩn bị bộ 500–1.000 cue Anh/Trung có bản dịch tham chiếu.
- Chạy đánh giá mù OpenAI, Gemini, DeepSeek, Groq và local trên cùng dữ liệu.
- Ghi lại chất lượng, độ trễ, số request và chi phí thực tế theo model.
- Chốt model mặc định sau đánh giá; không kết luận chất lượng chỉ dựa trên tên nhà cung cấp.
- Thực hiện kiểm thử với API key thật trong môi trường staging. Test tự động hiện dùng HTTP giả lập và không gửi transcript ra Internet.
