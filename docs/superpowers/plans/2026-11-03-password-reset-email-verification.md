# Implementation Plan — T33: Quên mật khẩu và xác minh email

Ngày chốt kế hoạch: **03/11/2026**  
Trạng thái: **Đã chốt thiết kế, chưa triển khai code**  
Phạm vi: ASP.NET Core/.NET 10, PostgreSQL, React 19, session cookie hiện hữu.

## 1. Mục tiêu và Definition of Done

T33 hoàn thành khi:

1. Người dùng có thể yêu cầu email đặt lại mật khẩu mà API không tiết lộ email có tồn tại hay không.
2. Link đặt lại mật khẩu dùng token ngẫu nhiên một lần, hết hạn, chỉ lưu hash trong bảng token và không xuất hiện trong log/referrer của API.
3. Đổi mật khẩu thành công thu hồi toàn bộ session hiện hữu và ghi audit atomically.
4. Tài khoản đăng ký bằng password mới phải xác minh email trước khi đăng nhập; tài khoản Google có claim `email_verified=true` được xem là đã xác minh.
5. Có luồng gửi lại email xác minh, rate limit và cooldown chống spam.
6. Email được xếp hàng bền vững sau transaction, có retry giới hạn; không gọi provider khi đang giữ database transaction/row lock.
7. UI responsive cho `/forgot-password`, `/reset-password`, `/verify-email` và trạng thái “hãy kiểm tra email”.
8. Có integration tests cho security, concurrency, replay và email worker; frontend tests, build, lint và Graphify đều đạt.

## 2. Các quyết định nghiệp vụ đã chốt

### 2.1 Tương thích tài khoản hiện có

- Migration backfill `email_verified_at = now()` cho toàn bộ account tồn tại trước T33 để không khóa người dùng hiện hành.
- Password registration sau T33 tạo account với `email_verified_at = NULL`, gửi email xác minh và chưa cấp session.
- Google registration đặt `email_verified_at` tại thời điểm tạo vì backend đã xác minh claim `email_verified=true`.
- Khi liên kết Google có email chuẩn hóa trùng account, có thể đánh dấu email account đã xác minh trong cùng transaction.
- Không triển khai đổi email trong T33.

### 2.2 Chính sách đăng nhập

- Account password chưa xác minh email không được đăng nhập; trả `403 EMAIL_NOT_VERIFIED` nhưng chỉ sau khi password đã được xác minh đúng.
- Sai email/password và account không tồn tại vẫn dùng `401 INVALID_CREDENTIALS` chung.
- Google login giữ chính sách hiện tại; không tự động link chỉ vì trùng email.
- Account `LOCKED` không được reset password hoặc xác minh để tự mở khóa. Request public vẫn trả response chung, worker không gửi email cho account bị khóa.

### 2.3 Password reset

- `POST /auth/password-reset/request` luôn trả `202 Accepted` với cùng nội dung cho email tồn tại/không tồn tại.
- Token có tối thiểu 256 bit entropy, TTL **15 phút**, dùng một lần.
- Request mới thu hồi token reset chưa dùng trước đó của cùng user; chỉ token mới nhất hợp lệ.
- Reset được phép tạo password cho account Google-only, giúp account có thêm phương thức đăng nhập.
- Password mới tuân thủ policy 12–128 ký tự hiện tại và không được trùng password đang có.
- Reset thành công cập nhật password, consume token, thu hồi toàn bộ session, ghi `PASSWORD_RESET_COMPLETED` trong một transaction.

### 2.4 Email verification

- Token verification TTL **24 giờ**, dùng một lần.
- `POST /auth/email-verification/resend` trả `202` chung và áp cooldown; không tiết lộ account/email/status.
- Confirm thành công đặt `email_verified_at`, consume token và ghi `EMAIL_VERIFIED` atomically.
- Replay token đã dùng/hết hạn trả lỗi chung `INVALID_OR_EXPIRED_TOKEN`; không phân biệt nguyên nhân.

### 2.5 Token không rò rỉ

- Database token table chỉ lưu `SHA-256(rawToken)`; so sánh dạng fixed-time ở application và unique index ở database.
- Link email dùng fragment: `/reset-password#token=...` và `/verify-email#token=...`. Fragment không được gửi trong HTTP request/referrer đến server.
- React đọc token từ `location.hash`, xóa fragment bằng `history.replaceState`, chỉ giữ token trong memory đến khi submit.
- Không log raw token, link đầy đủ, password, email body hoặc Data Protection payload.

## 3. Thiết kế dữ liệu — migration `005_auth_email_recovery.sql`

### 3.1 `store.user_accounts`

Thêm:

- `email_verified_at timestamptz NULL`.
- Backfill account hiện hữu trong migration rồi giữ nullable cho account mới.

### 3.2 `store.auth_action_tokens`

- `id uuid PK`
- `user_id uuid NOT NULL FK user_accounts ON DELETE CASCADE`
- `purpose varchar(32)` check `PASSWORD_RESET | VERIFY_EMAIL`
- `token_hash char(64) NOT NULL UNIQUE`
- `email_snapshot varchar(320) NOT NULL`
- `created_at timestamptz NOT NULL`
- `expires_at timestamptz NOT NULL`
- `consumed_at timestamptz NULL`
- `revoked_at timestamptz NULL`

Index `(user_id, purpose, expires_at)` và index cleanup theo `expires_at`. Mọi request tạo token khóa `user_accounts` trước, revoke token cùng purpose còn hiệu lực rồi insert token mới.

### 3.3 `store.auth_email_outbox`

- `id uuid PK`
- `user_id uuid NULL FK user_accounts ON DELETE SET NULL`
- `action_token_id uuid NULL FK auth_action_tokens ON DELETE SET NULL`
- `template_key varchar(64)` check `PASSWORD_RESET | VERIFY_EMAIL`
- `recipient_email varchar(320) NOT NULL`
- `protected_payload text NOT NULL`
- `created_at`, `next_attempt_at`, `sent_at`, `discarded_at`
- `attempt_count int NOT NULL DEFAULT 0`
- `last_error_code varchar(64) NULL`

`protected_payload` chứa raw token đã bảo vệ bằng ASP.NET Core Data Protection purpose riêng `HCC.AuthEmail.v1`; không lưu raw token/plain link. Worker giải mã ngay trước khi gửi và không log payload. Persistent key ring của T30 là điều kiện bắt buộc.

## 4. Backend contracts và API

### 4.1 Application contracts

Tạo module `Application/AuthRecovery` hoặc đặt cạnh Auth nếu giữ module hiện tại:

- `RequestPasswordResetRequest { email }`
- `ResetPasswordRequest { token, newPassword, confirmPassword }`
- `ResendVerificationRequest { email }`
- `ConfirmEmailRequest { token }`
- `IAuthRecoveryService`
- `IAuthEmailSender`
- `AuthRecoveryFailure` với mapping ProblemDetails chuẩn.

### 4.2 Endpoint dự kiến

| Method/path | Quyền | Kết quả |
|---|---|---|
| `POST /api/v1/auth/password-reset/request` | Public + CSRF + rate limit | Luôn `202` |
| `POST /api/v1/auth/password-reset/confirm` | Public + CSRF + rate limit | `204` hoặc lỗi token/password chung |
| `POST /api/v1/auth/email-verification/resend` | Public + CSRF + rate limit | Luôn `202` |
| `POST /api/v1/auth/email-verification/confirm` | Public + CSRF + rate limit | `204`; replay hợp lệ có thể idempotent nếu account đã verified và token đúng user |

Response request/resend dùng cùng thông điệp: “Nếu thông tin phù hợp, hướng dẫn sẽ được gửi đến email của bạn.”

### 4.3 Mã lỗi

- `400 VALIDATION_ERROR`
- `400 INVALID_OR_EXPIRED_TOKEN`
- `400 INVALID_PASSWORD`
- `400 CSRF_INVALID`
- `403 EMAIL_NOT_VERIFIED` chỉ ở login sau khi credential đúng
- `429 RATE_LIMITED` kèm `Retry-After`
- `503 EMAIL_DELIVERY_UNAVAILABLE` không trả ở request public; lỗi delivery được worker retry và quan sát nội bộ

## 5. Transaction và concurrency

### Request reset/resend

1. Normalize email.
2. Response public được cố định, kể cả account không tồn tại.
3. Nếu có active account: bắt đầu transaction, khóa user `FOR UPDATE`.
4. Recheck status/email verification; revoke token cũ cùng purpose.
5. Tạo raw token, lưu hash; tạo outbox với payload đã Data Protection protect.
6. Commit rồi đánh thức worker; không gọi email provider trong transaction.

### Confirm token

1. Hash token nhận được.
2. Transaction và `SELECT ... FOR UPDATE` token.
3. Khóa user sau token theo một lock order duy nhất `Token → User` cho mọi confirm/cleanup path.
4. Recheck purpose, expiry, consumed/revoked và account status.
5. Apply password/email verification, consume token, revoke sibling tokens, audit.
6. Commit. Password trigger hiện hữu thu hồi session khi `password_hash` đổi.

Hai confirm đồng thời: đúng một request thay đổi trạng thái; request còn lại nhận idempotent success cho verification hoặc `INVALID_OR_EXPIRED_TOKEN` cho reset, không bao giờ 500.

## 6. Email worker và cấu hình

- `AuthEmailDeliveryWorker` poll theo batch nhỏ bằng `FOR UPDATE SKIP LOCKED`.
- Retry exponential có jitter; tối đa 5 lần, sau đó `discarded_at` và structured log chỉ chứa outbox ID/template/error code.
- Bỏ job nếu token đã consumed/revoked/expired trước khi gửi.
- Development dùng Mailpit trong Docker Compose; production dùng adapter cấu hình qua secret (`SMTP` hoặc provider HTTP), không hard-code API key.
- Readiness không fail chỉ vì provider email tạm lỗi; metric/log phải thể hiện backlog, retry và discarded count.
- Cleanup worker xóa token/outbox payload đã hết retention; không xóa audit.

## 7. Rate limit và chống abuse

- Reset request: mặc định 5 request/giờ/IP.
- Verification resend: 3 request/giờ/IP và cooldown 60 giây/account.
- Confirm: 10 request/15 phút/IP.
- Luôn giữ response/body/timing gần tương đương giữa email tồn tại và không tồn tại; có thể thêm minimum response duration nhỏ, không dùng sleep trong DB transaction.
- Không tin `X-Forwarded-For` ngoài trusted proxy cấu hình T30.

## 8. Frontend

### Routes/components

- `/forgot-password`: form email, luôn hiển thị success neutral sau submit.
- `/reset-password`: đọc token fragment, input password + confirm + password checklist, show/hide password, disabled/busy/error states.
- `/verify-email`: tự confirm token fragment; trạng thái loading/success/expired và form resend.
- Sau đăng ký password: chuyển sang màn hình “Kiểm tra email” thay vì chỉ quay về login.
- Login khi nhận `EMAIL_NOT_VERIFIED`: thông báo rõ và CTA gửi lại email.

Tái sử dụng `AuthLayout`, `AuthInput`, password policy và style hiện tại. Các page mới dùng route lazy-loading để không tăng initial bundle.

## 9. Test matrix bắt buộc

### Backend integration tests

1. Request reset email có/không tồn tại/locked trả cùng `202` và schema response.
2. Token DB chỉ là hash; outbox payload không chứa raw token.
3. Reset đúng token đổi password, revoke mọi session, audit đúng; token replay thất bại sạch.
4. Token hết hạn/revoked/sai purpose trả lỗi chung, không đổi dữ liệu.
5. Hai confirm reset đồng thời chỉ một request thắng, không 500.
6. Hai resend đồng thời chỉ token mới nhất còn hợp lệ.
7. Password reset tạo password cho Google-only account nhưng không xóa identity.
8. Password registration mới chưa verified không login được; confirm xong login được.
9. Existing account migration vẫn login được; Google registration được verified ngay.
10. Email worker retry, `SKIP LOCKED`, không gửi trùng khi hai worker chạy đồng thời.
11. Rate limit request/resend/confirm và CSRF.
12. Token/password/email tuyệt đối không xuất hiện trong audit/log capture.

### Frontend tests

- Forgot form gửi đúng request và luôn hiển thị thông điệp neutral.
- Reset token được lấy khỏi fragment và fragment bị xóa khỏi URL.
- Password mismatch/policy/error/expired token.
- Verification success, invalid token và resend cooldown.
- Registration chuyển đúng màn hình kiểm tra email.
- Login `EMAIL_NOT_VERIFIED` hiển thị CTA resend.

### Browser/E2E

- Mailpit: register → mở email → verify → login.
- Forgot password → mở email → reset → session cũ bị từ chối → login password mới.
- Kiểm tra desktop/mobile và không có token trong network URL, console hoặc referrer.

## 10. Thứ tự triển khai đề xuất

1. Migration 005 + EF model/configuration + fixture apply script.
2. Contracts, token generator/hash/protector và provider abstraction.
3. Service transaction + exception mapping + endpoints/rate limit.
4. Auth email outbox worker + Mailpit/Compose/configuration.
5. Sửa register/login/Google-link theo `email_verified_at`.
6. React pages, routes và UX đăng ký/login.
7. Integration tests concurrency/security, frontend tests và browser Mailpit smoke.
8. Cập nhật auth API contract, deployment runbook, `.env.example`, README/TASKS.
9. Chạy `dotnet test`, `npm test`, lint/build, security scan liên quan và `python -m graphify update .`.

## 11. Không thuộc T33

- Đổi email tài khoản.
- MFA/TOTP/passkey.
- Marketing email/newsletter.
- Session/device dashboard.
- Admin tạo Staff/quản lý role.
- Distributed rate limiting; khi scale nhiều API instance phải chốt edge/shared limiter riêng.

## 12. Tiêu chí bắt đầu code

Trước khi implementation chỉ cần cấu hình local Mailpit; chưa cần API key production. Khi deploy thật phải chọn SMTP/provider, cung cấp secret qua environment/secret store, cấu hình public frontend origin cho action links và bảo đảm Data Protection key ring dùng chung/persistent.
