# Cấu hình SePay cho SubVid

SubVid dùng SePay cho thanh toán gói theo luồng: Desktop tạo checkout qua native host, Server sinh mã `SUBVID-##########`, Desktop hiển thị QR và polling, sau đó webhook SePay đối soát đúng mã + đúng số tiền trước khi kích hoạt subscription và đồng bộ cloud key.

## 1. Triển khai database

Triển khai các script theo thứ tự đến V8, sau đó chạy:

```powershell
sqlcmd -S <SQL_SERVER> -d TOOL_VIETSUB -E -b -i .\SubVid_SEPAY_V9.sql
```

V9 là script additive và idempotent. Script tạo `purchase_payment_transactions`, mở rộng ledger `payment_webhook_events`, bổ sung index/constraint đối soát và ghi schema version 9. Luôn sao lưu database và thử trên staging trước production.

## 2. Cấu hình bắt buộc

Section `SePay` hỗ trợ:

- `ApiBaseUrl`: API quản trị SePay, mặc định `https://my.sepay.vn`.
- `ApiToken`: token tùy chọn để Server tra cứu tài khoản nhận; không dùng cho webhook.
- `BankAccountId`: ID tài khoản ngân hàng trên SePay, nếu có.
- `QrBaseUrl`: dịch vụ dựng QR, mặc định `https://qr.sepay.vn`.
- `ReceiverBankShortName`, `ReceiverBankName`: mã ngắn và tên ngân hàng nhận.
- `ReceiverAccountNumber`, `ReceiverAccountName`: số và tên tài khoản nhận.
- `WebhookApiKey`: secret dùng xác thực webhook.
- `TransferCodePrefix`: bắt buộc là `SUBVID`.
- `PaymentExpireMinutes`: 5–120 phút, mặc định 15.

Không ghi token, webhook key hoặc tài khoản thật vào source control. Production sẽ không khởi động nếu thiếu webhook key hoặc thông tin tài khoản nhận.

### User Secrets cho máy phát triển

```powershell
dotnet user-secrets set "SePay:ApiToken" "<SEPAY_API_TOKEN>" --project .\SubVid.Server\SubVid.Server.csproj
dotnet user-secrets set "SePay:WebhookApiKey" "<WEBHOOK_API_KEY>" --project .\SubVid.Server\SubVid.Server.csproj
dotnet user-secrets set "SePay:BankAccountId" "<BANK_ACCOUNT_ID>" --project .\SubVid.Server\SubVid.Server.csproj
dotnet user-secrets set "SePay:ReceiverBankShortName" "<BANK_SHORT_NAME>" --project .\SubVid.Server\SubVid.Server.csproj
dotnet user-secrets set "SePay:ReceiverBankName" "<BANK_NAME>" --project .\SubVid.Server\SubVid.Server.csproj
dotnet user-secrets set "SePay:ReceiverAccountNumber" "<ACCOUNT_NUMBER>" --project .\SubVid.Server\SubVid.Server.csproj
dotnet user-secrets set "SePay:ReceiverAccountName" "<ACCOUNT_NAME>" --project .\SubVid.Server\SubVid.Server.csproj
```

### Environment variables cho production

```text
SUBVID_SEPAY_API_TOKEN
SUBVID_SEPAY_WEBHOOK_API_KEY
SUBVID_SEPAY_BANK_ACCOUNT_ID
SUBVID_SEPAY_RECEIVER_BANK_SHORT_NAME
SUBVID_SEPAY_RECEIVER_BANK_NAME
SUBVID_SEPAY_RECEIVER_ACCOUNT_NUMBER
SUBVID_SEPAY_RECEIVER_ACCOUNT_NAME
```

Các giá trị không nhạy cảm như URL và thời gian hết hạn có thể đặt bằng cấu hình .NET chuẩn, ví dụ `SePay__PaymentExpireMinutes`.

## 3. Cấu hình webhook trên SePay

URL production:

```text
POST https://<SUBVID_SERVER>/api/v1/payments/sepay/webhook
```

Đặt một trong các header sau với cùng giá trị `WebhookApiKey` của Server:

```text
X-Api-Key: <WEBHOOK_API_KEY>
Authorization: Apikey <WEBHOOK_API_KEY>
Authorization: Bearer <WEBHOOK_API_KEY>
```

Không cấu hình JWT cho endpoint này. Endpoint có rate limit, giới hạn payload 64 KiB và tự trả `200 ignored/unmatched/ambiguous` cho webhook hợp lệ nhưng chưa thể đối soát nhằm tránh retry vô hạn.

## 4. Chạy local không dùng tiền thật

`appsettings.Development.json` chỉ chứa tài khoản kiểm thử. Không chuyển tiền vào QR development.

1. Chạy `SubVid.Server` ở môi trường Development.
2. Đăng nhập Desktop, mở trang tài khoản và chọn một plan trả phí.
3. Ghi lại `transactionCode`, `amount` và order từ modal.
4. Gửi webhook mô phỏng vào localhost bằng payload riêng; `accountNumber` phải khớp tài khoản development và nội dung phải chứa đúng mã checkout.

Ví dụ payload, dùng ID mới cho một giao dịch mới:

```json
{
  "id": 900000001,
  "gateway": "TESTBANK",
  "transactionDate": "2026-08-20T10:00:00+07:00",
  "accountNumber": "0000000000",
  "code": "SUBVID-1234567890",
  "content": "SUBVID-1234567890",
  "transferType": "in",
  "transferAmount": 129000,
  "accumulated": 129000,
  "subAccount": null,
  "referenceCode": "LOCAL-900000001",
  "description": "Thanh toan SUBVID-1234567890"
}
```

Thay mã và số tiền bằng đúng checkout vừa tạo. Development cho phép webhook key rỗng; nên đặt một key giả qua User Secrets để kiểm tra cả xác thực.

Flow `FAKE_ADMIN` tại `/Admin/PurchaseTests` vẫn là lựa chọn an toàn để test settlement/subscription/cloud allocation mà không giả làm giao dịch SePay thật.

## 5. Kiểm tra checkout và replay webhook

- Checkout phải trả plan snapshot, ngân hàng, nội dung chuyển khoản, QR, hạn thanh toán và trạng thái; không trả token hoặc secret.
- Polling desktop dùng `GET /api/v1/purchases/{orderNumber}/status` qua native host và chỉ cho chủ đơn đọc.
- Gửi lại chính payload với cùng `id` hoặc `referenceCode` để test replay. Hệ thống phải trả kết quả idempotent và không tạo subscription/cloud allocation lần hai.
- Không đổi `id` khi test replay. Đổi `id` biểu diễn một provider event khác và có thể bị đối soát theo quy tắc riêng.
- Dùng trang `/Admin/Purchases` để kiểm tra order, transaction code, trạng thái payment, webhook gần nhất và subscription đã kích hoạt. Trang này không hiển thị secret.

## 6. Kiểm tra tự động

Unit/integration test không gọi SePay thật; gateway dùng `HttpMessageHandler` giả.

```powershell
cd .\SubVid.App\ClientApp
npm test
npm run build

cd ..\..
dotnet test .\SubVid.App.Tests\SubVid.App.Tests.csproj -c Release
dotnet build .\SubVid.Server\SubVid.Server.csproj -c Release
dotnet build .\SubVid.App\SubVid.App.csproj -c Release
```

Integration test dùng connection string từ `SUBVID_TEST_CONNECTION_STRING` hoặc cấu hình Server và yêu cầu schema V9.
