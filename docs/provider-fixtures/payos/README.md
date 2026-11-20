# PayOS provider contract evidence

Thư mục này chỉ lưu bằng chứng đã sanitize; không lưu Client ID, API key, checksum key, QR payload, số tài khoản hoặc checkout URL đầy đủ.

## Phiên kiểm chứng 09/11/2026

- API base quan sát: `https://api-merchant.payos.vn`.
- Official SDK: NuGet `payOS` phiên bản `2.1.0`, package owner `payOS`, repository `payOSHQ/payos-lib-dotnet`.
- SDK restore/build thành công trên `net10.0`, 0 warning và 0 error.
- SDK query với `MaxRetries = 0`, timeout 10 giây đọc và xác minh được response của payment request đã cancel.
- Raw HTTP create-link với canonical HMAC-SHA256 thành công; response `code=00`, status `PENDING`, có response signature, QR và checkout host `pay.payos.vn`.
- GET theo `orderCode` trả đúng amount/remaining và status `PENDING`.
- Cancel trả `CANCELLED`; GET sau cancel tiếp tục trả `CANCELLED` cùng cancellation reason.
- Request chữ ký thiếu/sai trả provider code `20` và không tạo payment link.

Fixture: [`create-query-cancel-2026-11-09.sanitized.json`](create-query-cancel-2026-11-09.sanitized.json).

## Chưa đủ bằng chứng để Accept ADR 0017

- Webhook thật có chữ ký do PayOS gửi tới endpoint của dự án.
- ACK/retry của webhook khi endpoint trả non-2xx/timeout.
- Payment thành công trong kênh test và query trạng thái `PAID`.
- Duplicate callback, late callback và callback amount mismatch.
- Kết quả create-link bị timeout sau khi provider đã nhận request và hành vi query phân giải.
- Cạnh tranh cancel với payment.

Các mục này phải hoàn tất trước khi bỏ `PAYOS_DEFERRED` hoặc bật `PayOs:Enabled` cho Customer.

## Quy tắc tái chạy

1. Credential chỉ được đọc từ `.env`/secret store đã ignore.
2. Mọi request tạo link dùng order code mới và số tiền test nhỏ.
3. Link chưa thanh toán phải được cancel sau probe.
4. Không commit raw response nếu có QR, checkout URL, account number/name hoặc signature gắn với dữ liệu chưa sanitize.
5. SDK phải cấu hình `MaxRetries = 0` cho create-link; ứng dụng tự phân giải trạng thái mơ hồ bằng query thay vì retry POST.

