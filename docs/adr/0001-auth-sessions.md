# ADR 0001 — Cookie HttpOnly và session PostgreSQL

Trạng thái: **Đã được chủ dự án đồng ý** ngày 30/10/2026. Phạm vi T07–T10.

## Quyết định

Cập nhật theo yêu cầu trực tiếp của chủ dự án: đăng ký cần `confirmPassword` khớp password; password mới (register/change/bootstrap) bắt buộc 12–128 ký tự, chữ A–Z, a–z, số 0–9 và ký tự đặc biệt ngoài chữ/số/khoảng trắng. Đây là thay đổi chính sách đã được yêu cầu, không áp để chặn login tài khoản cũ.

- React và API cùng origin. Local dùng Vite proxy; production dùng reverse proxy HTTPS.
- Cookie chỉ chứa token ngẫu nhiên 256 bit; HttpOnly, SameSite=Lax, Path=/, không Domain. Production dùng Secure và tên `__Host-hcc-session`; Development dùng `hcc-session` để hỗ trợ HTTP localhost.
- Database chỉ lưu SHA-256 của token, account ID, thời điểm tạo và hạn tuyệt đối 7 ngày. Không sliding expiration; không localStorage/JWT/refresh token.
- Mỗi request kiểm tra session chưa hết hạn và account ACTIVE trong PostgreSQL. Role đọc từ DB, không lấy từ client.
- Login xoay token, hủy phiên cũ của cùng trình duyệt; logout hủy phiên hiện tại. Đổi password/role/status hủy mọi phiên bằng trigger trong cùng transaction. Login và thay đổi thông tin nhạy cảm khóa account row để tránh session được tạo từ thông tin cũ.
- ASP.NET Core Identity PasswordHasher, PBKDF2 IdentityV3, 210000 iterations. Password 12–128 ký tự; không trim password. Email trim và lowercase, unique index lower(email) là guard cuối dưới concurrency.
- Đăng ký public chỉ CUSTOMER. Kết quả mới/trùng email đều 202 với thông báo chung, không tự đăng nhập. Login email sai/password sai/account locked dùng cùng lỗi 401.
- CSRF: GET `/api/v1/auth/csrf` lấy request token; mọi unsafe API request hiện tại phải có header `X-CSRF-TOKEN` và antiforgery cookie. Fetch token mới trước mỗi mutation để không dùng token gắn với identity cũ. Webhook PayOS tương lai cần cơ chế miễn CSRF rõ ràng và provider signature riêng.
- Chính sách backend Customer/Staff/Admin riêng, không tự cho Admin kế thừa mọi quyền Staff. Fallback yêu cầu xác thực; public catalog/csrf/register/login đánh dấu AllowAnonymous. Quyền DX chưa implement mặc định không mở.
- Rate limit login/register theo remote IP: 10/phút và 5/giờ; password change 5/phút. Bản đầu in-memory, một API instance; trước khi scale cần shared limit/edge limit và cấu hình trusted proxy. Không tin X-Forwarded-For từ client.
- Admin đầu tiên tạo bằng lệnh bootstrap tại server, đọc password từ input ẩn, không có default password/public bootstrap endpoint. Chỉ cho chạy khi chưa có Admin.

## Phạm vi endpoint mới

`GET auth/csrf`, `POST auth/register`, `POST auth/login`, `GET auth/me`, `POST auth/logout`, `POST auth/password`, `PATCH admin/accounts/{id}/status` (đường dẫn tương đối `/api/v1`). Password và CSRF là bổ sung kỹ thuật của ADR này. Chưa có reset password qua email, quản lý role/UI Admin, đăng ký Staff hay profile/address.

## Vận hành

Áp SQL nâng cấp `database/002_auth_sessions.sql` có transaction, không chạy lại baseline 001. Schema nguồn vẫn 31 business entities; `auth_sessions` là bảng hạ tầng bổ sung. API không tự chạy migration. Data Protection keys cần lưu bền vững/được bảo vệ và dùng chung nếu scale (antiforgery); token session không phụ thuộc key này. Dọn phiên hết hạn theo lịch vận hành ở T30; dù chưa dọn, phiên hết hạn không xác thực được.

## Tham khảo

- https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0

## Kiểm chứng bắt buộc

Register trùng/case/race, chống role injection, password hash, generic login error, cookie flags, CSRF thiếu/sai/cross-client, expiry/logout/replay, đổi password/role/lock rồi unlock không hồi sinh session, 401/403, Admin status audit, bootstrap một lần, rate limiting và FE redirect/guards/logout. Database test riêng, không sửa account của người dùng trong DB ứng dụng.
