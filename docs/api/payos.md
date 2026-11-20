# PayOS Payment API — T19/T21

Trạng thái: **T20/T21 backend và frontend runtime đã triển khai, feature flag mặc định tắt**. Webhook hợp lệ được lưu durable inbox rồi áp dụng guarded transition nguyên tử. Production gate vẫn cần sandbox E2E callback thật và provider GET `PAID` có transaction reference đã xác minh.

## Cấu hình

```text
PayOs__Enabled=false
PayOs__ClientId=<secret>
PayOs__ApiKey=<secret>
PayOs__ChecksumKey=<secret>
PayOs__ReturnUrl=https://shop.example.com/payment/payos/return
PayOs__CancelUrl=https://shop.example.com/payment/payos/return?cancel=1
PayOs__RequestTimeoutSeconds=10
PayOs__AttemptResolutionDelaySeconds=15
PayOs__CheckoutUrlAllowedHosts__0=pay.payos.vn
```

- Khi `Enabled=false`, checkout PAYOS tiếp tục trả `409 PAYOS_DEFERRED`.
- Production bắt buộc Return/Cancel URL HTTPS.
- Credential chỉ lấy từ secret source; không trả về frontend hoặc log.
- Official SDK `payOS 2.1.0` được pin và cấu hình `MaxRetries=0`.

## Customer API

Tất cả endpoint Customer yêu cầu session role `CUSTOMER`. Mutation yêu cầu CSRF.

### GET `/api/v1/payments/providers`

Endpoint anonymous, `no-store`, chỉ trả capability tối thiểu `{ "payOs": { "enabled": true|false } }`. Không trả credential, URL ký, checksum hay thông tin cấu hình nhạy cảm. Frontend dùng contract này để bật/tắt lựa chọn PayOS.

### POST `/api/v1/customer/orders/{orderId}/payment/payos-attempts`

Tạo hoặc replay payment attempt owner-only.

- Transaction 1: khóa Order -> Payment, tạo attempt/provider order code rồi commit.
- Gọi PayOS ngoài transaction.
- Transaction 2: khóa attempt và lưu link đã kiểm tra HTTPS/host.
- Attempt `LINK_READY` replay trả `200` và `Idempotency-Replayed: true`.
- Attempt `CREATED` mới hơn resolution delay trả `409 PAYMENT_ATTEMPT_IN_PROGRESS`; không query provider khi request create đầu còn in-flight.
- Kết quả create mơ hồ giữ `CREATED`; retry sau delay dùng GET provider, không POST create lần hai.

Response `201/200`:

```json
{
  "attemptId": "uuid",
  "attemptNo": 1,
  "status": "LINK_READY",
  "checkoutUrl": "https://pay.payos.vn/...",
  "expiresAt": "2026-11-09T05:30:00Z",
  "requiresProviderQuery": false
}
```

Errors chính:

- `404 PAYMENT_NOT_FOUND`: không tồn tại, sai owner hoặc không phải PayOS.
- `409 PAYOS_DISABLED`: feature chưa bật.
- `409 PAYMENT_NOT_PENDING`, `PAYMENT_EXPIRED`, `PAYMENT_ATTEMPT_LIMIT_REACHED`.
- `409 PAYMENT_ATTEMPT_IN_PROGRESS` kèm `Retry-After`.
- `503 PAYMENT_STATE_UNRESOLVED`: create-link timeout/429/5xx; không được tạo Order mới.
- `502 PAYMENT_PROVIDER_MISMATCH`, `PAYMENT_LINK_INVALID`.

### GET `/api/v1/customer/orders/{orderId}/payment`

Trả trạng thái authoritative từ database, dùng được cả khi feature flag đã tắt để Customer theo dõi Order cũ. Không gọi provider và không yêu cầu provider credential.

### POST `/api/v1/customer/orders/{orderId}/payment/cancel`

Body: `{ "reason": "Khách đổi ý" }`.

- Gọi PayOS ngoài transaction database.
- Chỉ chuyển Order sang `CANCELLED`, Payment sang `VOID` và release Inventory/Voucher/Flash Sale khi provider trả trạng thái authoritative `CANCELLED`.
- Nếu callback paid thắng race, API trả `409 PAYMENT_ALREADY_PAID`; tuyệt đối không release inventory.
- Timeout/429/5xx hoặc trạng thái chưa rõ trả `503 PAYMENT_STATE_UNRESOLVED`; Order vẫn giữ nguyên.

## Provider webhook

### POST `/api/v1/integrations/payos/webhook`

- `[AllowAnonymous]`, miễn CSRF có chủ đích và rate limit riêng.
- Body tối đa 64 KiB.
- Kiểm tra field/range/currency/signature format trước SDK verification.
- Signature được xác minh bằng checksum key qua official SDK.
- Payload lưu DB là safe projection; bỏ account number/name, counter-account và raw QR/credential.
- SHA-256 raw JSON có unique index; callback đồng thời/trùng trả 2xx sau durable insert, chỉ một event tồn tại.
- Signature sai được lưu `REJECTED` để audit rồi trả `400 PAYOS_INVALID_SIGNATURE`.
- Chỉ payload có chữ ký đúng, `success=true`, envelope/data code `00`, amount/currency/payment-link khớp mới có quyền áp transition.
- Payment hợp lệ trước deadline: callback `APPLIED`, Payment `SUCCESS`, Order `CONFIRMED`, Reservation/Inventory `HELD -> ALLOCATED`, Flash Sale `HELD -> SOLD`, đúng một transaction `VERIFIED`.
- Amount/payment-link mismatch mở reconciliation và không xác nhận Order.
- Callback paid sau `VOID/CANCELLED/EXPIRED` chuyển Payment `LATE_PAID`, callback `LATE_PAID`, mở reconciliation; không hồi sinh Order hoặc inventory.
- Callback trùng, kể cả đồng thời, không nhân đôi transaction/movement/notification.

ACK:

```json
{ "success": true, "duplicate": false }
```

## Admin reconciliation API — T22

Tất cả endpoint yêu cầu role `ADMIN`; mutation yêu cầu CSRF và response `no-store`.

- `GET /api/v1/admin/payos-reconciliation?page=1&pageSize=25&status=OPEN&reason=LATE_PAID&q=...`
- `GET /api/v1/admin/payos-reconciliation/{id}`
- `POST /api/v1/admin/payos-reconciliation/{id}/claim`
- `POST /api/v1/admin/payos-reconciliation/{id}/resolve` với `{ "note": "..." }`
- `POST /api/v1/admin/payos-reconciliation/{id}/manual-refund` với `{ "evidenceReference": "...", "note": "..." }`

Lock order claim/resolve là `Payment -> PaymentReconciliationCase`; refund có thể đóng một Order còn giữ hàng nên dùng `Order -> Payment -> PaymentReconciliationCase -> PaymentTransaction -> Voucher/Flash Sale -> SKU -> Inventory`. Mỗi case mới liên kết đúng một transaction và mỗi manual refund liên kết đúng case đó. Claim có lease 30 phút; Admin khác chỉ takeover sau khi lease cũ hết hạn. Evidence/note được trim và kiểm tra lại tại service, ngoài validation HTTP. `LATE_PAID`/`DUPLICATE_COLLECTION` không được resolve bằng note đơn thuần. Manual refund V1 là ghi nhận thao tác đã thực hiện ngoài hệ thống, không gọi payout/refund API PayOS; amount lấy từ transaction `RECONCILIATION_REQUIRED`, không nhận số tiền tùy ý từ browser.

Nếu refund một `AMOUNT_MISMATCH` khi Order còn `PENDING_PAYMENT`, cùng transaction sẽ đóng Order, release Voucher/Flash Sale/reserved inventory và chuyển Payment sang `REFUNDED`. Refund `DUPLICATE_COLLECTION` giữ nguyên Payment gốc `SUCCESS`; chứng từ refund chỉ áp dụng cho khoản thu trùng.

## Invariants hiện có

- Provider call không chạy trong DB transaction.
- `provider_order_code` lấy từ sequence DB và nằm trong max-safe integer của SDK.
- Tối đa một attempt active (`CREATED`/`LINK_READY`) trên mỗi Payment.
- Provider payment-link ID và callback payload hash có unique index.
- Create-link không retry tự động.
- Return URL và query string từ provider chưa có quyền thay đổi Payment; trang `/payment/payos/return` luôn đọc trạng thái owner-only từ backend.
- Worker deadline query PayOS ngoài transaction; `PENDING` được cancel trước khi release. Provider lỗi/mơ hồ hoặc trả `PAID` nhưng chưa có transaction reference đã kiểm chứng thì giữ nguyên stock để đối soát, không đoán trạng thái.
- Fulfillment chấp nhận PayOS khi `Order=CONFIRMED`, `Payment=SUCCESS`; giao thành công không yêu cầu hoặc tạo khoản thu COD.
- `PayOs:Enabled=false` vẫn là mặc định; UI PayOS tồn tại nhưng bị vô hiệu hóa cho tới khi capability endpoint xác nhận feature đã bật.

Runbook vận hành: [operations/payos.md](../operations/payos.md).

