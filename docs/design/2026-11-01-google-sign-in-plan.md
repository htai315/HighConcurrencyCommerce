# Kế hoạch đăng nhập Google

Ngày: 01/11/2026. Trạng thái: đề xuất triển khai, chưa thay đổi code/schema.

## Hiện trạng đã xác minh

- PostgresAuthService tạo opaque session 7 ngày, DB chỉ giữ SHA-256 token; SessionCookie HttpOnly, SameSite=Lax, Secure theo môi trường.
- CsrfFilter áp dụng toàn cục cho mutation; frontend authMutation lấy token CSRF trước POST.
- user_accounts.password_hash hiện NOT NULL; UserAccount.PasswordHash không nullable. Chưa có external identity.
- Trigger database/002_auth_sessions.sql thu hồi session khi password/role/status thay đổi, dùng IS DISTINCT FROM.
- loginAction gộp giỏ guest sau đăng nhập, giữ giỏ local khi merge lỗi; safeReturnTo hạn chế redirect nội bộ.

## Phương án đề xuất

Google Identity Services (GIS), nút chính thức với JavaScript callback. React nhận ID token, POST về API cùng CSRF hiện có. Backend xác minh rồi cấp session nội bộ như đăng nhập mật khẩu. Không dùng Google token làm bearer token cho API, không lưu token vào localStorage/log. Không cần client secret trong flow này. One Tap và tự động đăng nhập ngoài phạm vi đợt đầu.

Backend kiểm tra chữ ký bằng thư viện được duy trì (ưu tiên Google.Apis.Auth, xác minh API/version trước triển khai), issuer, audience từ cấu hình server, expiry, subject và email_verified. Thêm challenge nonce ngẫu nhiên phía server, hết hạn ngắn, ràng buộc cookie trình duyệt HttpOnly, purpose login/link và user đích nếu link; consume atomically một lần khi thành công. Challenge không chứa identity do client tự khai. Dùng bảng challenge có expiry/cleanup để chạy nhiều instance; không chỉ giữ trong RAM. Token lỗi không tạo session/account. Lỗi mạng tới Google tách khỏi thông tin đăng nhập sai.

## Quy tắc tài khoản

1. Đợt đầu chỉ CUSTOMER. Không cấp STAFF/ADMIN từ Google/email/domain, không đổi role hiện có.
2. Nhận diện bằng (provider=GOOGLE, subject=sub), không dùng email làm khóa external identity.
3. Identity đã liên kết: khóa account, kiểm tra ACTIVE/CUSTOMER, tạo session; không tự ghi đè email/tên/role theo Google.
4. Identity mới, email chưa tồn tại: tạo CUSTOMER và identity trong cùng transaction; không yêu cầu mật khẩu giả. Tên lấy từ claim đã xác minh, chuẩn hóa/giới hạn độ dài; thiếu tên thì yêu cầu bổ sung. Điện thoại/địa chỉ bổ sung trước checkout qua luồng hiện có.
5. Email trùng account: không tự gộp. Hướng dẫn đăng nhập bằng phương thức hiện có và chủ động liên kết trong Bảo mật. Không tiết lộ role/trạng thái nội bộ.
6. Liên kết cần session CUSTOMER và xác minh lại mật khẩu hiện tại, Google token/challenge mới; email chuẩn hóa phải khớp email tài khoản trong phạm vi phiên bản đầu. Identity đã thuộc user khác phải bị từ chối.
7. Google-only: PasswordHash null; ẩn form đổi mật khẩu yêu cầu mật khẩu cũ, hiển thị phương thức đăng nhập. Chưa làm đặt mật khẩu mới/quên mật khẩu trong đợt này. Cho ngắt Google chỉ khi còn mật khẩu đăng nhập; yêu cầu re-auth, khóa account, audit và thu hồi session. Không cho bỏ phương thức đăng nhập cuối cùng.
8. Với email bên thứ ba Google không quản lý, không coi email_verified là bảo chứng quyền sở hữu hộp thư hiện tại. Không tự liên kết; khi cần dùng email cho recovery sau này phải có luồng xác minh riêng.

## Schema và concurrency

- Migration tăng dần: password_hash nullable; cập nhật EF mapping/model, PasswordService xử lý null và trả failed cho password login khi không có mật khẩu; kiểm tra bootstrap, change-password và trigger hồi quy.
- external_identities: id, user_id FK, provider, subject, created_at; unique(provider, subject), unique(user_id, provider).
- Giữ unique lower(email) hiện có. Tạo account + identity + session + consume challenge trong transaction. Hai lần đăng nhập đầu đồng thời không được tạo tài khoản mồ côi hoặc 500; bắt unique violation, rollback và đọc lại trong transaction mới có giới hạn retry.
- Mọi link/unlink/login kiểm tra trạng thái dưới khóa user; thống nhất thứ tự khóa challenge rồi user, kiểm tra deadlock với login/password/status hiện có. Không giữ transaction DB trong lúc tải public keys từ Google.
- Tách helper cấp session để password và Google dùng cùng expiry/rotation/revocation. Audit login/link/unlink không chứa credential/token.

## API dự kiến

| Endpoint | Vai trò |
|---|---|
| GET /api/v1/auth/providers | Trả Google enabled + public clientId; thiếu cấu hình thì ẩn nút |
| POST /api/v1/auth/google/challenge | CSRF + rate limit; tạo nonce/challenge theo purpose |
| POST /api/v1/auth/google | credential + challengeId; trả AuthUser và Set-Cookie |
| GET /api/v1/account/auth-methods | CUSTOMER; hasPassword, googleLinked |
| POST /api/v1/account/auth-methods/google | CUSTOMER, CSRF, password re-auth + credential/challenge; liên kết |
| DELETE /api/v1/account/auth-methods/google | CUSTOMER, CSRF, password re-auth; chặn nếu là phương thức cuối |

Chuẩn ProblemDetails + correlationId, no-store và rate limit. Lỗi dự kiến: GOOGLE_AUTH_INVALID, GOOGLE_AUTH_UNAVAILABLE, ACCOUNT_LINK_REQUIRED, IDENTITY_ALREADY_LINKED, LAST_AUTH_METHOD. Thiết kế HTTP status cụ thể trong API contract khi triển khai. Không cho payload quyết định userId hoặc role.

## Chia task triển khai

1. Chốt ADR, API contract, schema migration và cấu hình feature flag. Ghi rõ cách rollout/rollback: tắt feature an toàn, không ép NOT NULL trở lại khi đã có Google-only users.
2. Viết verifier/challenge/session helper và service Google login. Test độc lập validation; test transaction trên PostgreSQL thật.
3. Viết quản lý link/unlink và auth-methods; xử lý password null toàn bộ flow.
4. React GoogleSignInButton tái sử dụng trên Login/Register, tải SDK một lần, cleanup callback khi unmount, trạng thái loading/error/popup đóng/script bị chặn. Nút theo branding Google, responsive với auth layout hiện có. Thông báo rõ tiếp tục có thể tạo tài khoản; đồng bộ điều khoản với đăng ký thường.
5. Tách finishSignIn dùng chung cho password/Google: merge giỏ, giữ giỏ khi merge lỗi, lấy CSRF mới sau đổi identity, refresh root loader/header và safeReturnTo. Nếu đã login không cho silent chuyển tài khoản ngoài ý muốn.
6. Bảo mật tài khoản hiển thị phương thức thực, thao tác liên kết/ngắt liên kết có xác nhận; Google-only không hiện form đổi mật khẩu không thể sử dụng.
7. Kiểm thử, cập nhật README/docs/api/docs/TASKS, Graphify sau code cuối và build/lint/tests.

## Ma trận kiểm thử và tiêu chí hoàn tất

- Token hợp lệ/sai chữ ký/audience/issuer/hết hạn/email chưa xác minh/nonce thiếu, sai, hết hạn, replay; CSRF thiếu, rate limit, Google unavailable.
- New/returning customer; email trùng cần link; tài khoản locked; role khác; link identity của người khác; ngắt phương thức cuối bị chặn.
- Đồng thời: cùng sub, cùng email khác sub, password registration tranh Google registration, link/unlink tranh khóa tài khoản; không orphan/duplicate/session của account locked.
- Password login/register/change-password/logout và thu hồi session giữ hành vi đúng sau schema nullable.
- FE: SDK lỗi không làm mất form password, hủy popup không báo đăng nhập thành công, giữ returnTo, merge lỗi không mất giỏ, badge/session cập nhật.
- Integration dùng verifier fake có ranh giới rõ; kiểm tra verifier thật bằng token fixture/ký test phù hợp, không coi fake là đã kiểm tra Google. Smoke tài khoản Google thật cần clientId được cấu hình; không tự động vượt màn hình Google consent.
- Build/lint/tests và browser responsive; Google thật ở Chrome/Edge, kiểm tra FedCM/popup policy và môi trường HTTPS triển khai.

## Cấu hình bên ngoài

Tạo OAuth client loại Web application trong Google Cloud, cấu hình Branding/Audience, test users nếu cần và Authorized JavaScript origins. Local ưu tiên thống nhất http://localhost:5173; cấu hình frontend/API/proxy/cookie cùng hostname, không đổi qua lại localhost/127.0.0.1 giữa phiên. Bổ sung origin production HTTPS. Callback JavaScript không cần redirect endpoint Google. Client ID là public, backend giữ allowlist audience riêng; không đưa client secret vào VITE_*.

Rà CSP/COOP theo GIS, mở đúng nguồn cần thiết, không nới toàn bộ policy. Thiếu cấu hình provider phải ẩn nút rõ ràng; password login vẫn hoạt động.

## Nguồn chính thức

- https://developers.google.com/identity/gsi/web/guides/verify-google-id-token
- https://developers.google.com/identity/gsi/web/guides/get-google-api-clientid
- https://developers.google.com/identity/gsi/web/reference/html-reference
- https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/google-logins?view=aspnetcore-10.0

Không thay auth hiện có bằng ASP.NET Core Identity chỉ để bổ sung Google. AddGoogle server redirect là lựa chọn khác; chưa chọn vì GIS callback phù hợp React và CSRF/session hiện tại, không cần thêm external cookie scheme.
