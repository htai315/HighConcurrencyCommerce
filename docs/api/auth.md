# Auth/session contract — ADR 0001

Base `/api/v1`. [Quyết định đã được duyệt](../adr/0001-auth-sessions.md). JSON camelCase; auth/admin responses `Cache-Control: no-store`. Các endpoint dưới đây đã implement; profile/address, quản lý role/Staff, quên mật khẩu/email verification chưa có.

## Endpoint

| Method/path | Quyền | Body / kết quả |
|---|---|---|
| GET `/auth/csrf` | Public | 200 `{requestToken}` và antiforgery cookie HttpOnly |
| POST `/auth/register` | Public + CSRF | `{email,password,confirmPassword,fullName}`; 202 cùng thông báo cho email mới/trùng, không auto-login |
| POST `/auth/login` | Public + CSRF | `{email,password}`; 200 User DTO và session cookie mới, phiên cũ của trình duyệt bị hủy |
| GET `/auth/me` | Authenticated | 200 User DTO; guest/expired/revoked/locked → 401, không redirect HTML |
| POST `/auth/logout` | Authenticated + CSRF | 204, hủy phiên hiện tại và xóa cookie; thiết bị khác vẫn còn phiên |
| POST `/auth/password` | Authenticated + CSRF | `{currentPassword,newPassword}`; 204, hủy mọi phiên, cần login lại |
| PATCH `/admin/accounts/{id}/status` | Admin + CSRF | `{status:"ACTIVE" hoặc "LOCKED"}`; 204, audit cùng transaction; không đổi trạng thái chính mình |

User DTO: `{id: UUID, email: string, fullName: string, role: "CUSTOMER"|"STAFF"|"ADMIN"}`. Không chứa token/password/hash. Token session chỉ nằm trong Set-Cookie.

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
| 401 `INVALID_CREDENTIALS` | Email không có, password sai, account locked: cùng lỗi |
| 401 `UNAUTHORIZED` | Không có session hợp lệ |
| 403 `FORBIDDEN` | Role không đủ quyền |
| 404 `NOT_FOUND` | Admin cập nhật account không tồn tại |
| 409 `SELF_STATUS_CHANGE` | Admin đổi trạng thái chính mình |
| 429 `RATE_LIMITED` | Quá ngưỡng; có Retry-After |

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

- `/login`, `/register`, `/account/security` có form thật; header lấy `/me`, logout qua CSRF.
- `/orders` chỉ Customer, `/staff` chỉ Staff, `/admin` chỉ Admin. Các dashboard/orders vẫn là khung nội dung, chưa có API nghiệp vụ tương ứng. Guard không thay thế backend authorization.
- Sau login có thể trở về đường dẫn nội bộ `returnTo`; chặn URL ngoài origin/protocol-relative/backslash. Auth/role không được lấy từ URL hoặc localStorage.
- Chưa có password reset/email verification, session dashboard, cleanup worker, production key-management/deployment hoặc E2E toàn bộ nghiệp vụ. Đã có browser smoke auth desktop/mobile, không tạo tài khoản thật. T30 cần persistent Data Protection keys cho antiforgery và lịch xóa session hết hạn. Session hết hạn bị từ chối ngay dù row chưa được dọn.
