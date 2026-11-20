# Implementation Plan — T34: Quản trị tài khoản nội bộ và Staff

Ngày chốt kế hoạch: **03/11/2026**  
Trạng thái: **Đã chốt thiết kế, chưa triển khai code**  
Phụ thuộc: T09 session/RBAC, T30 deployment baseline, T33 auth-email outbox.

## 1. Vì sao T34 là bước tiếp theo

Hệ thống đã có role `STAFF`, policy backend và dashboard vận hành nhưng chưa có quy trình an toàn để Admin tạo Staff. Hiện Staff chỉ có thể xuất hiện qua thao tác database thủ công. PayOS T19–T22 vẫn chủ động hoãn, vì vậy hoàn thiện vòng đời tài khoản nội bộ là bước mang lại giá trị lớn nhất tiếp theo.

## 2. Mục tiêu và Definition of Done

T34 hoàn thành khi:

1. Admin xem, tìm kiếm và lọc danh sách Customer/Staff/Admin với phân trang ổn định.
2. Admin mời Staff mới bằng email và họ tên; không có mật khẩu mặc định.
3. Staff nhận email token một lần, tự đặt mật khẩu và xác minh email trước lần đăng nhập đầu.
4. Admin gửi lại lời mời, khóa/mở account và thu hồi toàn bộ session của target.
5. Không có API tạo Admin, truyền role tùy ý hoặc nâng Customer hiện hữu thành Staff.
6. Mọi mutation recheck Admin actor trong transaction, ghi audit và an toàn dưới concurrency.
7. Admin Dashboard có workspace quản trị tài khoản responsive và accessible.
8. Backend/frontend tests, lint/build, Compose validation và Graphify đều đạt.

## 3. Quyết định nghiệp vụ đã chốt

### 3.1 Phạm vi role

- API chỉ tạo role `STAFF`; role do server gán, request không có trường `role`.
- Admin mới chỉ được tạo bằng `--bootstrap-admin`; không mở endpoint tạo/promote Admin.
- Email đã thuộc Customer/Staff/Admin trả `409 EMAIL_ALREADY_EXISTS`; không chuyển Customer thành Staff vì account có thể đang sở hữu dữ liệu commerce.
- Không xóa cứng account. Account có lịch sử chỉ được `LOCKED`.
- Không cho Admin khóa hoặc thu hồi phiên của chính mình qua endpoint target.

### 3.2 Vòng đời lời mời Staff

- Khi mời: tạo `UserAccount` role `STAFF`, `account_status=ACTIVE`, `password_hash=NULL`, `email_verified_at=NULL`.
- Account chưa có password không thể login; Google auth vẫn chỉ dành cho Customer.
- Tạo action token purpose `STAFF_INVITE`, TTL 24 giờ, chỉ lưu SHA-256 hash.
- Outbox template `STAFF_INVITE` chứa token được Data Protection bảo vệ và dùng worker T33.
- Staff mở `/staff-invitation#token=...`, tự đặt password. Confirm thành công đặt password, `email_verified_at`, consume token và audit atomically.
- Resend revoke token cũ, discard outbox chưa gửi và tạo token/outbox mới.
- Nếu Admin khóa account trước khi accept, token không dùng được. Mở khóa không hồi sinh token hết hạn/revoked; Admin phải resend.

### 3.3 Trạng thái hiển thị

API trả lifecycle dẫn xuất:

- `INVITATION_PENDING`: Staff chưa có password hoặc chưa verified.
- `ACTIVE`: account active và có phương thức đăng nhập hợp lệ.
- `LOCKED`: account bị khóa.

Không thêm `INVITED` vào check constraint `account_status`; lifecycle chỉ là DTO projection để không phá các guard hiện hữu.

### 3.4 Session management

- Admin chỉ thấy `activeSessionCount`, thời điểm session mới nhất và hạn gần nhất; không trả token hash/cookie/IP/User-Agent.
- `POST .../sessions/revoke` xóa toàn bộ session target trong transaction và ghi audit.
- Block self-target để Admin không vô tình tự làm mất phiên giữa mutation.

## 4. Migration `006_internal_staff_invites.sql`

Migration additive và chạy lặp an toàn:

1. Mở rộng check constraint `auth_action_tokens.purpose` thêm `STAFF_INVITE`.
2. Mở rộng check constraint `auth_email_outbox.template_key` thêm `STAFF_INVITE`.
3. Thêm index `(role_id, account_status, created_at DESC, id DESC)` nếu query plan chứng minh cần.
4. Không backfill hoặc thay đổi role/status/password account hiện hữu.
5. Nhúng migration vào Infrastructure, IntegrationTests, `--migrate-auth`, fixture và Compose init.

PostgreSQL không hỗ trợ thêm value trực tiếp vào CHECK constraint; migration phải drop/recreate constraint bằng tên xác định trong transaction. Phải test chạy migration hai lần.

## 5. Backend contracts và API

### 5.1 DTO/request

- `AdminAccountListItemDto`: id, email, fullName, role, accountStatus, lifecycleStatus, emailVerifiedAt, createdAt, activeSessionCount, latestSessionAt.
- `AdminAccountPageDto`: items, page, pageSize, totalCount.
- `InviteStaffRequest`: email, fullName.
- `AcceptStaffInvitationRequest`: token, password, confirmPassword.
- `AdminAccountFailure` và `IAdminAccountService`; không tiếp tục làm phình `IAuthService`.

### 5.2 Endpoint

| Method/path | Quyền | Kết quả |
|---|---|---|
| `GET /api/v1/admin/accounts` | Admin | Search/filter role/status/lifecycle, paging |
| `GET /api/v1/admin/accounts/{id}` | Admin | Chi tiết an toàn, không token/hash |
| `POST /api/v1/admin/staff-invitations` | Admin + CSRF | `201`, tạo Staff + token + outbox |
| `POST /api/v1/admin/accounts/{id}/staff-invitation/resend` | Admin + CSRF | `202`, chỉ Staff pending |
| `PATCH /api/v1/admin/accounts/{id}/status` | Admin + CSRF | Giữ route hiện hữu, chuyển sang service mới |
| `POST /api/v1/admin/accounts/{id}/sessions/revoke` | Admin + CSRF | `204`, revoke toàn bộ session target |
| `POST /api/v1/auth/staff-invitation/accept` | Public + CSRF + rate limit | `204`, đặt password và verify email |

### 5.3 Query validation

- `q`: trim, tối đa 100 ký tự, tìm email/full name.
- `role`: `CUSTOMER|STAFF|ADMIN`; `status`: `ACTIVE|LOCKED`.
- `lifecycle`: `INVITATION_PENDING|ACTIVE|LOCKED`.
- Sort cố định `created_at DESC, id DESC`; page size mặc định 25, tối đa 100.

## 6. Transaction, lock order và concurrency

### Mutation trên target hiện hữu

Lock order thống nhất:

1. Actor Admin `user_accounts FOR UPDATE`.
2. Target `user_accounts FOR UPDATE`.
3. Action token/outbox liên quan.
4. Sessions và audit.

Sau lock phải recheck actor `ACTIVE + ADMIN`, target, self-target và business state.

### Tạo Staff

- Khóa actor/recheck quyền trước.
- Role Staff lấy từ DB, không nhận từ client.
- Unique index `lower(email)` là authority chống race.
- Hai request cùng email: đúng một `201`, request kia `409 EMAIL_ALREADY_EXISTS`; không 500 hoặc token/outbox mồ côi.
- Account, token, outbox và audit `STAFF_INVITED` commit cùng transaction.

### Accept invitation

Lock order `ActionToken → UserAccount` giống T33:

- Recheck purpose, expiry, consumed/revoked, account active, role Staff, password null và email snapshot.
- Hash password, set verified, consume/revoke sibling token, discard outbox, audit `STAFF_INVITATION_ACCEPTED` atomically.
- Hai confirm đồng thời: đúng một `204`; request kia `400 INVALID_OR_EXPIRED_TOKEN`, không 500.

### Resend và lock race

- Resend khóa actor → target rồi revoke token cũ trước khi tạo token mới.
- Lock account và accept tranh chấp trên target; branch lấy target lock trước quyết định. Không để Staff locked nhưng invitation vẫn accept xuyên transaction.

## 7. Security và rate limit

- Token tối đa 128 ký tự, TTL 24 giờ, dùng một lần.
- Link fragment `/staff-invitation#token=...`; frontend xóa fragment sau khi đọc.
- Không log raw token, protected payload, password hoặc email đầy đủ.
- Invite/resend/status/revoke dùng Admin policy và CSRF.
- Accept: 10 request/15 phút/IP, queue 0, có `Retry-After`.
- Danh sách account `Cache-Control: no-store`.
- Audit: `STAFF_INVITED`, `STAFF_INVITATION_RESENT`, `STAFF_INVITATION_ACCEPTED`, `ACCOUNT_STATUS_CHANGED`, `ACCOUNT_SESSIONS_REVOKED`.

## 8. Email và worker

- Mở rộng sender bằng template Staff invitation hoặc chuyển sang typed template request.
- Email ghi rõ lời mời Staff, TTL 24 giờ và hướng dẫn liên hệ Admin nếu không mong đợi.
- Tái sử dụng lease/outbox at-least-once T33; token một lần bảo vệ API khi email gửi lặp.
- `PUBLIC_WEB_URL` tiếp tục là nguồn tạo link Compose/production.

## 9. Frontend

### Admin Dashboard

Thêm workspace “Tài khoản & Nhân sự”:

- Search email/họ tên; filter role/status/lifecycle.
- Bảng/card responsive: account, role, lifecycle, verified, active sessions, createdAt.
- Form mời Staff với confirm dialog.
- Action resend, lock/unlock, revoke sessions; disabled khi busy và block self-action trên UI, backend vẫn là authority.
- Pagination và loading/empty/error/success rõ ràng.

### Staff invitation page

- Route lazy `/staff-invitation` dùng AuthLayout.
- Đọc token fragment vào memory rồi xóa URL.
- Password + confirmation + checklist, show/hide, busy/error/expired.
- Thành công chuyển `/login?staffInvited=1`.

## 10. Test matrix bắt buộc

### Backend integration

1. Guest/Customer/Staff bị chặn; Admin được list/detail/invite.
2. List/filter/search/paging ổn định, DTO không lộ hash/token.
3. Invite tạo đúng Staff/password null/token hash/outbox protected/audit.
4. Hai invite cùng email: `201 + 409`, không dữ liệu mồ côi.
5. Không invite email Customer hiện hữu hoặc truyền role/Admin.
6. Accept token đặt password + verify; login Staff thành công; replay thất bại.
7. Token hết hạn/revoked/sai purpose/role trả lỗi chung.
8. Hai accept đồng thời chỉ một thắng.
9. Resend revoke link cũ; link mới hoạt động; cooldown/rate limit.
10. Lock-vs-accept concurrency bảo toàn trạng thái.
11. Status/revoke session block self-target, revoke session thật và audit đúng.
12. Migration 006 chạy lặp không sửa dữ liệu hiện hữu.
13. Worker gửi template Staff invitation và link đúng `PUBLIC_WEB_URL`.

### Frontend

- Admin loader/query/filter/paging.
- Invite form gửi CSRF và refresh list.
- Resend/lock/unlock/revoke confirm + busy/error.
- Invitation token fragment bị xóa, password policy và success redirect.
- Responsive/accessibility smoke desktop/mobile.

## 11. Thứ tự triển khai

1. Migration 006, persistence mapping và fixture/migrate/Compose wiring.
2. Contracts + `IAdminAccountService` + exception mapping.
3. List/detail queries.
4. Invite/resend/accept transaction và email template.
5. Chuyển status mutation từ AuthService + thêm revoke sessions.
6. Backend integration tests, đặc biệt concurrency/race.
7. Frontend API client, Admin workspace và invitation page.
8. Frontend tests/browser smoke.
9. Cập nhật README, TASKS, auth contract và deployment config.
10. Chạy toàn bộ test/build/lint, Compose validation và Graphify.

## 12. Ngoài phạm vi T34

- Tạo/promote/demote Admin qua web.
- Chuyển Customer có dữ liệu commerce thành Staff.
- Xóa cứng account hoặc dữ liệu cá nhân.
- MFA/passkey.
- Session dashboard theo thiết bị/IP/User-Agent; cần migration metadata và privacy policy riêng.
- SSO doanh nghiệp hoặc Google login cho Staff/Admin.

## 13. Kết quả mong đợi

Sau T34: bootstrap Admin đầu tiên → Admin mời Staff → Staff tự đặt mật khẩu qua email → Staff vận hành sản phẩm/kho/đơn → Admin khóa hoặc thu hồi phiên khi nhân sự nghỉ việc. Không cần thao tác trực tiếp database và không tồn tại default credential.
