# ADR 0002 — Xác thực Google Identity Services (GIS) và External Identities

Trạng thái: **Đã triển khai, đã sửa lỗi review và kiểm thử tự động** ngày 01/11/2026. Chưa nghiệm thu đăng nhập Google thật với OAuth Client ID của môi trường triển khai.

## Ngữ cảnh

Hệ thống `HighConcurrencyCommerce` sử dụng kiến trúc session cookie opaque HttpOnly lưu trong bảng `store.auth_sessions` trên PostgreSQL (ADR 0001). Cần bổ sung phương thức đăng nhập bằng Google cho khách hàng (CUSTOMER) mà không phá vỡ mô hình session hiện tại, không đổi sang JWT bearer cho API nội bộ, và bảo vệ an toàn trước các rủi ro giả mạo danh tính, race condition và rò rỉ bảo mật.

## Quyết định

1. **Kiến trúc xác thực:**
   - Sử dụng Google Identity Services (GIS) Web SDK với nút bấm chính thức của Google và JavaScript callback.
   - Luồng xác thực: Trình duyệt nhận Google ID token (JWT) -> gửi POST lên API nội bộ cùng header `X-CSRF-TOKEN` và challenge cookie -> Backend xác minh chữ ký, audience, issuer, expiry, subject, email_verified và nonce -> tạo hoặc tìm tài khoản -> cấp opaque session cookie 7 ngày (`__Host-hcc-session` / `hcc-session`) giống như đăng nhập mật khẩu.
   - Google ID token chỉ dùng để xác minh danh tính ở bước đăng nhập; không dùng làm bearer token cho API nội bộ, không lưu token vào database, localStorage, sessionStorage hay log.
   - Không yêu cầu Google Client Secret trong luồng xác thực trình duyệt này (chỉ cấu hình OAuth Client ID).
   - Chưa triển khai One Tap hoặc tự động đăng nhập ngầm trong đợt đầu.

2. **Server-side Nonce Challenge & Replay Protection:**
   - Trước khi hiển thị nút Google, client gọi `POST /api/v1/auth/google/challenge` để nhận `challengeId` và `nonce`.
   - Backend sinh nonce 256-bit ngẫu nhiên, lưu vào bảng `store.auth_challenges` với TTL 5 phút, purpose (`LOGIN` hoặc `LINK`), và token hash bảo vệ gắn với HttpOnly cookie `hcc-challenge` (`__Host-hcc-challenge`).
   - Khi token Google gửi lên, một conditional UPDATE kiểm tra id/purpose/user/token hash/expiry/consumed_at và đánh dấu consume atomically; chỉ request cập nhật đúng 1 dòng được tiếp tục. Không dựa vào SELECT FOR UPDATE ngoài transaction. Challenge đã consume không được trả lại khi Google lỗi: client phải bắt đầu lượt mới.
   - Mọi thao tác login/link/unlink khóa user trước khi sửa identity. Login đọc identity để tìm user, lấy khóa user rồi kiểm tra identity còn tồn tại trước khi cấp session; không giữ khóa identity rồi chờ user.
   - Sau unique violation lúc tạo tài khoản, rollback và clear EF ChangeTracker trước lượt retry; không giữ entity Added của transaction thất bại.

3. **Mô hình tài khoản & Role Guard:**
   - Đợt đầu chỉ hỗ trợ vai trò `CUSTOMER`. Tuyệt đối không cấp hoặc suy luận `STAFF` hoặc `ADMIN` từ email hay Google claims.
   - Bảng `store.external_identities` lưu ánh xạ `(provider = 'GOOGLE', subject = sub)` với unique index `(provider, subject)` và `(user_id, provider)`.
   - Cột `store.user_accounts.password_hash` được chuyển sang nullable để hỗ trợ tài khoản chỉ đăng nhập bằng Google (Google-only). Tuyệt đối không tạo mật khẩu giả hoặc mật khẩu mặc định.
   - Đăng ký mới qua Google: tạo `user_accounts` với `password_hash = NULL`, role `CUSTOMER`, và bản ghi `external_identities` trong cùng transaction; audit log `USER_REGISTERED_GOOGLE`.
   - Người dùng cũ qua Google: đăng nhập đúng account đã liên kết, kiểm tra `account_status == 'ACTIVE'`, audit log `GOOGLE_LOGIN`.
   - Trùng email chưa liên kết: Nếu email đã tồn tại trong hệ thống dưới dạng tài khoản mật khẩu nhưng chưa liên kết Google identity, hệ thống từ chối đăng nhập với lỗi `ACCOUNT_LINK_REQUIRED` (409). Tuyệt đối không tự động liên kết tài khoản chỉ vì trùng email. Người dùng phải đăng nhập bằng mật khẩu hiện tại rồi chủ động liên kết trong trang Bảo mật.

4. **Liên kết & Hủy liên kết (Account Linking & Unlinking):**
   - Chỉ cho phép thao tác trên tài khoản `CUSTOMER` đang có phiên đăng nhập hợp lệ.
   - **Liên kết:** Yêu cầu xác thực mật khẩu hiện tại (nếu tài khoản đã có mật khẩu) cùng challenge mục đích `LINK`. Email chuẩn hóa của Google phải khớp email tài khoản đang đăng nhập. Không cho liên kết Google identity đã thuộc về tài khoản khác (`IDENTITY_ALREADY_LINKED`). Audit log `GOOGLE_IDENTITY_LINKED`.
   - **Hủy liên kết:** Yêu cầu xác thực lại mật khẩu hiện tại. Chặn hủy liên kết nếu tài khoản chưa có mật khẩu (`LAST_AUTH_METHOD` - không cho phép hủy phương thức đăng nhập duy nhất). Khi hủy liên kết thành công, thu hồi toàn bộ session của tài khoản đó trong cơ sở dữ liệu (`DELETE FROM store.auth_sessions WHERE user_id = @id`) và xóa cookie phiên để bảo vệ người dùng; audit log `GOOGLE_IDENTITY_UNLINKED`.
   - **Chống chuyển tài khoản ngầm:** Nếu người dùng đang có phiên đăng nhập của User A, nhưng thực hiện Google Login dẫn tới User B, hệ thống từ chối với lỗi `SESSION_CONFLICT` (409) nhằm ngăn chặn việc âm thầm chuyển tài khoản ngoài ý muốn.

5. **Xử lý đồng thời & Concurrency:**
   - Bounded retry loop (tối đa 3 lần) bắt lỗi PostgreSQL unique violation (mã lỗi `23505`), rollback transaction và thử lại, đảm bảo hai request đăng ký Google đồng thời cùng `sub` hoặc cùng `email` không gây lỗi 500 hoặc tạo bản ghi mồ côi.
   - Thư viện `Google.Apis.Auth` (`GoogleJsonWebSignature`) tải và kiểm tra chữ ký public keys ngoài transaction DB để tránh giữ lock cơ sở dữ liệu trong lúc chờ mạng.

## Thay đổi cơ sở dữ liệu

- Migration `database/004_google_auth.sql`:
  - `ALTER TABLE store.user_accounts ALTER COLUMN password_hash DROP NOT NULL;`
  - Bảng `store.external_identities`: `id` (PK, UUID), `user_id` (FK, ON DELETE CASCADE), `provider` (VARCHAR(32)), `subject` (VARCHAR(255)), `email` (VARCHAR(320)), `created_at` (TIMESTAMPTZ). Unique `(provider, subject)` và `(user_id, provider)`.
  - Bảng `store.auth_challenges`: `id` (PK, UUID), `nonce` (VARCHAR(64), UNIQUE), `purpose` (VARCHAR(16)), `user_id` (UUID NULL), `token_hash` (VARCHAR(64)), `created_at` (TIMESTAMPTZ), `expires_at` (TIMESTAMPTZ), `consumed_at` (TIMESTAMPTZ NULL). Index trên `expires_at`.

## Thay đổi API

- `GET /api/v1/auth/providers`: Trả về trạng thái bật/tắt của các provider và Google Client ID công khai.
- `POST /api/v1/auth/google/challenge`: Tạo nonce challenge và thiết lập HttpOnly cookie `hcc-challenge`.
- `POST /api/v1/auth/google`: Tiếp nhận `credential` và `challengeId`, xác minh và cấp session cookie.
- `GET /api/v1/account/auth-methods`: Trả về trạng thái `hasPassword`, `googleLinked`, `googleEmail`.
- `POST /api/v1/account/auth-methods/google`: Liên kết tài khoản Google (yêu cầu mật khẩu re-auth nếu có).
- `DELETE /api/v1/account/auth-methods/google`: Hủy liên kết Google (yêu cầu mật khẩu re-auth, chặn nếu là phương thức cuối, thu hồi toàn bộ session).

