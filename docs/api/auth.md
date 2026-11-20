# Auth/session contract — ADR 0001

Base `/api/v1`. [Quyết định đã được duyệt](../adr/0001-auth-sessions.md). JSON camelCase; auth/admin responses `Cache-Control: no-store`. Các endpoint trong bảng dưới đây đã implement đầy đủ. Profile/address đã có contract riêng; quản lý role/Staff đầy đủ chưa có. Quên mật khẩu và xác minh email thuộc T33 đã được triển khai hoàn chỉnh cùng outbox delivery worker.

## Endpoint

| Method/path | Quyền | Body / kết quả |
|---|---|---|
| GET `/auth/csrf` | Public | 200 `{requestToken}` và antiforgery cookie HttpOnly |
| GET `/auth/providers` | Public | 200 `{google: {enabled, clientId}}` |
| POST `/auth/google/challenge` | Public + CSRF | `{purpose: "LOGIN"|"LINK"}`; 200 `{challengeId, nonce}` và cookie HttpOnly `hcc-challenge` |
| POST `/auth/google` | Public + CSRF | `{credential, challengeId}`; 200 User DTO và session cookie mới |
| POST `/auth/register` | Public + CSRF | `{email,password,confirmPassword,fullName}`; 202 cùng thông báo cho email mới/trùng, không auto-login; đẩy email xác minh vào outbox |
| POST `/auth/login` | Public + CSRF | `{email,password}`; 200 User DTO và session cookie mới, phiên cũ của trình duyệt bị hủy (từ chối 403 nếu email chưa xác minh) |
| POST `/auth/password-reset/request` | Public + CSRF | `{email}`; 202 `{message}`, anti-enumeration đồng nhất cho email có/không tồn tại |
| POST `/auth/password-reset/confirm` | Public + CSRF | `{token,newPassword,confirmPassword}`; 204, đặt lại mật khẩu, thu hồi toàn bộ phiên cũ |
| POST `/auth/email-verification/resend` | Public + CSRF | `{email}`; 202 `{message}`, anti-enumeration đồng nhất |
| POST `/auth/email-verification/confirm` | Public + CSRF | `{token}`; 204, kích hoạt tài khoản thành công |
| GET `/auth/me` | Authenticated | 200 User DTO; guest/expired/revoked/locked → 401, không redirect HTML |
| POST `/auth/logout` | Authenticated + CSRF | 204, hủy phiên hiện tại và xóa cookie; thiết bị khác vẫn còn phiên |
| POST `/auth/password` | Authenticated + CSRF | `{currentPassword,newPassword}`; 204, hủy mọi phiên, cần login lại |
| GET `/account/auth-methods` | Customer | 200 `{hasPassword, googleLinked, googleEmail}` |
| POST `/account/auth-methods/google` | Customer + CSRF | `{credential, challengeId, currentPassword?}`; 204, liên kết tài khoản Google |
| DELETE `/account/auth-methods/google` | Customer + CSRF | `{currentPassword}`; 204, hủy liên kết Google, thu hồi toàn bộ session |
| PATCH `/admin/accounts/{id}/status` | Admin + CSRF | `{status:"ACTIVE" hoặc "LOCKED"}`; 204, audit cùng transaction; không đổi trạng thái chính mình |
| GET `/admin/accounts` | Admin | Phân trang/lọc: `q`, `role`, `status`, `lifecycle`, `page`, `pageSize`; 200 `{items, page, pageSize, totalCount}` |
| GET `/admin/accounts/{id}` | Admin | 200 Chi tiết tài khoản nội bộ `{id, email, fullName, phone, role, accountStatus, lifecycleStatus, ...}` |
| POST `/admin/staff-invitations` | Admin + CSRF | `{email, fullName}`; 201 Chi tiết tài khoản Staff kèm outbox email mời kích hoạt một lần |
| POST `/admin/accounts/{id}/staff-invitation/resend` | Admin + CSRF | 204, thu hồi token cũ, phát sinh token mới và gửi lại email kích hoạt (cooldown 60s) |
| POST `/admin/accounts/{id}/sessions/revoke` | Admin + CSRF | 204, thu hồi toàn bộ phiên đăng nhập của người dùng mục tiêu; cấm tự thu hồi chính mình |
| POST `/auth/staff-invitation/accept` | Public + CSRF | `{token, password, confirmPassword}`; 204, xác minh email, đặt mật khẩu và kích hoạt tài khoản Staff |

User DTO: `{id: UUID, email: string, fullName: string, role: "CUSTOMER"|"STAFF"|"ADMIN", emailVerified: boolean}`. Cờ xác minh giúp UI khóa sớm các kênh yêu cầu verified email; backend vẫn revalidate khi mutation. DTO không chứa token/password/hash. Token session chỉ nằm trong Set-Cookie.

Email trim, lowercase, tối đa 320 ký tự; unique index `lower(email)` chống race. Họ tên không rỗng, tối đa 160. Theo cập nhật của chủ dự án: password đăng ký/mới 12–128 ký tự, có chữ hoa A–Z, chữ thường a–z, số 0–9 và ký tự không phải chữ/số/khoảng trắng (ví dụ @). Không trim password. Register bắt buộc `confirmPassword` khớp chính xác; không lưu trường xác nhận. Login/current password tối đa 128, vẫn xác minh mật khẩu cũ. Đổi mật khẩu yêu cầu password mới khác password hiện tại. JSON `role`/`userId` từ client không cấp quyền và không chọn tài khoản thay mật khẩu.

## Cookie và CSRF

- Production/Testing: `__Host-hcc-session`, Secure, HttpOnly, SameSite=Lax, Path=/, không Domain; Development HTTP: `hcc-session` không Secure. Hạn tuyệt đối 7 ngày, không sliding. PostgreSQL chỉ lưu SHA-256 token ngẫu nhiên 32 bytes.
- Antiforgery cookie tương ứng `__Host-hcc-csrf` hoặc `hcc-csrf`, HttpOnly. JavaScript lấy request token từ response GET `/auth/csrf`, gửi header `X-CSRF-TOKEN` cho mọi POST/PATCH/PUT/DELETE hiện tại. Không đọc cookie bằng JS.
- FE lấy token mới trước mỗi mutation; không cache token qua thay đổi identity. Token phải khớp antiforgery cookie và identity của session; token của trình duyệt khác không hợp lệ.
- GET không làm thay đổi account/session. GET `/auth/csrf` có thể tạo antiforgery cookie nhưng không tạo phiên đăng nhập.
- Cookie không nằm trong localStorage/sessionStorage. FE dùng cùng origin; không mở CORS wildcard hoặc gửi credentials sang domain khác.

## Thu hồi và đồng thời

Login khóa account row trước xác minh và tạo session. Password/status đổi trong transaction với account row lock. PostgreSQL trigger trên password_hash, role_id, account_status xóa tất cả session của account trong cùng transaction, bao gồm thay đổi trực tiếp qua công cụ quản trị. Khóa rồi mở lại không hồi sinh cookie cũ. Đọc `/me` không gia hạn.

Admin đổi trạng thái khóa actor + target theo ID, rồi kiểm tra lại actor ACTIVE/ADMIN trong transaction; ghi audit không chứa password/token. Admin không tự khóa/mở chính mình. Bootstrap dùng advisory transaction lock để hai process không đồng thời tạo hai Admin đầu tiên; không nâng quyền email đang có.

Request đã bắt đầu trước khi bị thu hồi không thể bị “rút lại” tự động. Những command nhạy cảm phải tiếp tục kiểm tra actor/state trong transaction; các command password/status hiện tại đã thực hiện. Profile/address/checkout phải bổ sung ownership khi triển khai, không suy ra từ việc FE ẩn menu.

## Lỗi

ProblemDetails có `status`, `title`, `code`, `correlationId` và `errors` với validation. FE ánh xạ thông báo an toàn, không render nguyên lỗi kỹ thuật từ server.

| Status/code | Trường hợp |
|---|---|
| 400 `VALIDATION_ERROR` | Field sai định dạng/giới hạn |
| 400 `CSRF_INVALID` | Thiếu/sai antiforgery header/cookie/identity; lấy token mới rồi thử lại |
| 400 `INVALID_PASSWORD` | Current password sai hoặc new password trùng |
| 400 `GOOGLE_AUTH_INVALID` | Credential Google không hợp lệ hoặc hết hạn |
| 400 `INVALID_CHALLENGE` | Challenge nonce không hợp lệ, hết hạn hoặc sai browser token |
| 400 `LAST_AUTH_METHOD` | Không thể hủy liên kết Google khi tài khoản chưa có mật khẩu |
| 400 `EMAIL_MISMATCH` | Email Google liên kết không khớp email tài khoản đang đăng nhập |
| 400 `CANNOT_CREATE_ADMIN` | Không cho phép tạo hoặc nâng quyền Admin qua API quản trị |
| 400 `CUSTOMER_CANNOT_BE_STAFF` | Email đã là khách hàng, không thể tự chuyển đổi thành Staff qua lời mời |
| 400 `INVALID_OR_EXPIRED_TOKEN` | Token mời Staff không tồn tại, đã sử dụng, đã bị thu hồi hoặc đã quá hạn 24h |
| 400 `ACCOUNT_LOCKED` | Không thể kích hoạt tài khoản đang ở trạng thái LOCKED |
| 401 `INVALID_CREDENTIALS` | Email không có, password sai, account locked: cùng lỗi |
| 401 `UNAUTHORIZED` | Không có session hợp lệ |
| 403 `FORBIDDEN` | Role không đủ quyền |
| 404 `NOT_FOUND` | Admin cập nhật account không tồn tại |
| 409 `EMAIL_ALREADY_EXISTS` | Email đã tồn tại trong hệ thống |
| 409 `ACCOUNT_LINK_REQUIRED` | Email đã có tài khoản mật khẩu, yêu cầu đăng nhập mật khẩu rồi liên kết trong Bảo mật |
| 409 `IDENTITY_ALREADY_LINKED` | Identity Google đã được liên kết với tài khoản khác |
| 409 `SESSION_CONFLICT` | Đang đăng nhập tài khoản khác; không cho silent switch tài khoản |
| 409 `SELF_STATUS_CHANGE` | Admin đổi trạng thái chính mình |
| 409 `SELF_REVOKE_PROHIBITED` | Admin không được phép thu hồi toàn bộ phiên đăng nhập của chính mình |
| 429 `RATE_LIMITED` | Quá ngưỡng; có Retry-After |
| 503 `GOOGLE_AUTH_UNAVAILABLE` | Lỗi mạng kết nối tới Google JWK server |

Rate limit mặc định theo remote IP: login 10/phút, register 5/giờ, password 5/phút; queue=0. Login/register cho phép cấu hình qua `Auth:LoginPermitLimit`, `Auth:RegisterPermitLimit` (tests dùng ngưỡng cao riêng và test riêng ngưỡng mặc định). Bộ đếm in-memory theo API process; chưa phải rate limit distributed. Không tin X-Forwarded-For tự gửi; T30 phải cấu hình trusted proxies và edge/shared limiter trước scale.

## Chạy local / nâng cấp DB

Với database đã có baseline 001 và User Secrets `ConnectionStrings:StoreDb`, từ `HighConcurrencyCommerce/`:

```powershell
dotnet run --project .\HighConcurrencyCommerce.Api -- --migrate-auth
dotnet run --project .\HighConcurrencyCommerce.Api --launch-profile http
```

`--migrate-auth` chỉ cho host loopback; áp `002_auth_sessions.sql` embedded trong build, có transaction, không tạo account. Script có thể chạy lại, không chạy lại baseline 001 trên DB đã có. API bình thường không tự migrate. Local workspace này đã áp upgrade 002 ngày 30/10/2026.

Tạo Admin đầu tiên bằng lệnh riêng (không cần chạy API server trước):

```powershell
dotnet run --project .\HighConcurrencyCommerce.Api -- --bootstrap-admin
```

Lệnh hỏi email, họ tên, password và xác nhận password; input password được ẩn. Hỗ trợ stdin khi tự động hóa, không truyền password qua argv, không lưu vào source. Chỉ tạo khi chưa có Admin (kể cả Admin locked vẫn được tính); nếu email đã có thì từ chối nâng quyền. Không chạy bootstrap với thông tin giả trên DB của người dùng. Test đã thực hiện bootstrap trên DB tạm riêng.

## Frontend và giới hạn

### Sửa lỗi Google Auth sau review — 01/11/2026

- POST/DELETE `/account/auth-methods/google` dùng chung policy `password` (5 request/phút/IP/process), bao gồm giới hạn re-auth khi ngắt liên kết.
- Challenge consume bằng conditional UPDATE atomic. Sai/hết hạn/đã dùng trả `INVALID_CHALLENGE`; verifier thất bại cũng cần challenge mới. UI có nút `Thử lại Google`, hết hạn chủ động sau 4 phút trước TTL server 5 phút, không refresh nền liên tục.
- Login/Register và liên kết dùng chung SDK loader + GoogleSignInButton; nhập mật khẩu không tạo challenge mới. SDK lỗi có timeout và có thể tải lại. Callback sau unmount và callback trùng trong cùng lượt bị bỏ qua.
- `/account/auth-methods` vẫn chỉ Customer. Trang Bảo mật Staff/Admin không gọi endpoint này và vẫn giữ form đổi mật khẩu; không hiển thị liên kết Google cho role nội bộ.
- Chủ dự án đã xác nhận Google Sign-In hoạt động với OAuth Client ID local. Browser regression tự động vẫn cố ý dùng GIS/API fixtures để chạy lặp ổn định và không phụ thuộc Google bên ngoài.

- `/login`, `/register`, `/account/security` có form thật; header lấy `/me`, logout qua CSRF.
- `/orders` chỉ Customer, `/staff` chỉ Staff, `/admin` chỉ Admin. Các trang này đã nối API nghiệp vụ tương ứng; route guard frontend không thay thế backend authorization.
- Sau login có thể trở về đường dẫn nội bộ `returnTo`; chặn URL ngoài origin/protocol-relative/backslash. Auth/role không được lấy từ URL hoặc localStorage.
- Password reset và email verification (T33) đã hoàn tất runtime code, transactional outbox và worker nền; chưa có session dashboard quản lý phiên đang mở hoặc E2E toàn bộ nghiệp vụ. T30 đã có persistent Data Protection keys và deployment baseline. Session và action token hết hạn bị từ chối ngay dù row chưa được dọn.

## Hợp đồng gửi Email xác minh & Đặt lại mật khẩu (T33)

- **Cơ chế Outbox & Worker:** Email được lưu vào outbox trong cùng database transaction với hành động tạo tài khoản hoặc yêu cầu reset. Background worker claim theo batch nhỏ (5 items) bằng transaction ngắn có lease time-bounded (`locked_at`, `locked_by`), giải phóng DB transaction ngay trước khi gọi SMTP bên ngoài.
- **Tính chất phân tán (At-Least-Once Delivery):** SMTP trong môi trường mạng phân tán vốn chỉ bảo đảm *at-least-once*. Worker xử lý và commit kết quả (`sent_at`, giải phóng lease) ngay sau từng email được gửi đi; áp dụng timeout 30s cho từng email để tổng thời gian batch không bao giờ vượt lease 5 phút. Trường hợp worker crash ngay sau khi SMTP provider nhận thư nhưng trước khi DB commit có thể khiến email được gửi lại khi lease hết hạn, nhưng action token 1 lần (hash SHA-256) bảo đảm chỉ có tối đa 1 lần sử dụng thành công trên API, không bao giờ phát sinh lỗ hổng bảo mật.
- **Cấu hình môi trường:** API container trong Docker Compose nhận `AuthEmail__FrontendBaseUrl` qua biến `PUBLIC_WEB_URL` (mặc định `http://localhost:8080` cho Compose local; môi trường production cần cấu hình URL HTTPS thực tế).
