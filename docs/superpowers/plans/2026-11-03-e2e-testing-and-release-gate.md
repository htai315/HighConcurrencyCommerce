# Implementation Plan — T35: Kiểm thử E2E và Release Gate

Ngày chốt kế hoạch: **03/11/2026**  
Trạng thái: **Đang triển khai**  
Phụ thuộc: T01–T18, T23–T28, T30 (Deployment baseline), T31–T34 (Customer Order Center, Google Auth, Password Recovery, Staff Management).  
Task tiếp theo sau T35: T36 (Đánh giá sản phẩm & rating) → T37 (Wishlist) → T38 (Voucher cơ bản) → T19–T22 (PayOS).

---

## 1. Vì sao T35 là bước tiếp theo

Hệ thống đã hoàn tất đầy đủ các module cốt lõi của một sàn thương mại điện tử chuyên nghiệp:
- Quản trị danh mục & sản phẩm (T12)
- Quản lý kho, nhập kho & điều chỉnh tồn (T13–T14)
- Giỏ hàng & sáp nhập giỏ (T15)
- Flash sale & phân bổ tồn kho flash sale (T23–T24)
- Checkout đa SKU giữ tồn (T17)
- Đơn hàng COD, xác nhận & hủy tự động (T18, T25–T26)
- Thông báo in-app (T27)
- Báo cáo doanh thu & kiểm toán (T28)
- Customer Order Center (T31)
- Google Sign-In bảo mật qua challenge nonce (T32)
- Quên mật khẩu & xác minh email qua Transactional Outbox (T33)
- Quản trị tài khoản nội bộ & nhân sự Staff (T34)

Với 196 backend integration tests và 81 frontend unit/component tests đang pass, hệ thống có độ bao phủ logic rất cao. Tuy nhiên, hiện tại **chưa có bộ kiểm thử E2E thực sự chạy trên toàn bộ stack Docker thật (Browser → Frontend Nginx → Backend API → PostgreSQL 18 → Mailpit SMTP)** và chưa có **CI Pipeline tự động chặn merge (Release Gate)**. Việc thiết lập T35 là cần thiết để bảo đảm tính toàn vẹn của hệ thống trước khi tiếp tục mở rộng tính năng mới.

---

## 2. Mục tiêu và Definition of Done

T35 hoàn thành khi đạt đầy đủ 5 trụ cột:

1. **Bộ Playwright E2E chạy trên live stack thật:**
   - Test thật, không mock HTTP request (`page.route`), tương tác với backend, database và Mailpit API thật.
   - Luồng 1: Khách đăng ký tài khoản → nhận email verify trên Mailpit → bấm link `#token=...` kích hoạt → đăng nhập thành công.
   - Luồng 2: Quên mật khẩu → nhận email trên Mailpit → đổi mật khẩu thành công → đăng nhập bằng mật khẩu mới.
   - Luồng 3: Google Sign-In smoke: kiểm tra nút GIS, render an toàn, route guard và fallback.
   - Luồng 4: Admin mời Staff → Staff nhận email trên Mailpit → truy cập trang `#token=...` đặt mật khẩu → kích hoạt tài khoản → đăng nhập Staff Dashboard.
   - Luồng 5: Khách hàng xem sản phẩm → thêm vào giỏ → thực hiện Checkout COD giữ tồn kho thành công.
   - Luồng 6: Staff vận hành mở danh sách đơn COD → xác nhận COD → đóng gói (packing) → giao hàng (dispatch) → giao thành công & thu tiền COD.
   - Luồng 7: Khách hàng mở trang Đơn hàng (`/orders`) xem đơn hàng đã giao và xem thông báo (`/notifications`).

2. **Cài đặt & kiểm tra từ Database trống (Fresh DB lifecycle):**
   - Áp tuần tự migration từ `001` đến `006` trên database PostgreSQL mới hoàn toàn.
   - Chạy lại các migration để chứng minh tính idempotent (không lỗi, không ghi đè dữ liệu hợp lệ).
   - Seed dữ liệu demo đầy đủ: danh mục, sản phẩm vợt/giày/phụ kiện, tồn kho khả dụng, và 3 tài khoản demo ổn định (`admin@badminton.local`, `staff@badminton.local`, `customer@badminton.local` với mật khẩu an toàn `DemoPass123!@#`).
   - Khởi động thành công toàn bộ stack qua `docker compose up -d` với healthcheck healthy.

3. **CI Pipeline hoàn chỉnh (GitHub Actions):**
   - Workflow `.github/workflows/ci.yml` tự động kích hoạt trên Pull Request và Push.
   - Job Backend: Build .NET 10 solution + chạy 196 integration tests với PostgreSQL service.
   - Job Frontend: Lint oxlint (0 errors, 0 warnings), Vitest (81 tests pass), Production build (`tsc -b && vite build`).
   - Job Docker Compose: `docker compose config` hợp lệ, build containers thành công.
   - Job E2E Smoke: Chạy Playwright E2E suite kiểm tra các luồng nghiệp vụ chính trên Docker stack.
   - Chặn merge nếu bất kỳ bước nào thất bại.

4. **Security Hardening & Secret Cleanup:**
   - Quét toàn bộ repository, loại bỏ mọi secret thô.
   - Google GIS chỉ dùng `GoogleAuth:ClientId` (public identifier). Không lưu Google Client Secret trong repo, `.env.example` hay frontend. Hướng dẫn xoay vòng secret đã từng xuất hiện trong lịch sử chat.
   - Đảm bảo các security header chuẩn: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, SameSite HttpOnly cookies, CSRF token validation trên mọi POST/PATCH/DELETE.

5. **One-click Demo & Release Checklist:**
   - Script chạy demo 1 lệnh `scripts/demo-up.ps1` (Windows) và `scripts/demo-up.sh` (Linux/macOS) tự động dựng stack và seed dữ liệu.
   - Tài liệu Release Checklist và hướng dẫn kiểm thử tại `docs/operations/release-checklist.md` và `docs/operations/e2e-testing-guide.md`.

---

## 3. Thiết kế chi tiết từng trụ cột

### 3.1 Dữ liệu Demo Seed (`database/007_demo_seed.sql`)
- Danh mục sản phẩm:
  - Vợt cầu lông (Badminton Rackets)
  - Giày cầu lông (Badminton Shoes)
  - Phụ kiện cầu lông (Accessories & Strings)
- Sản phẩm mẫu & biến thể SKU:
  - Vợt Yonex Astrox 88D Pro (SKU 3U, 4U; giá 4.290.000đ; tồn kho 50 cái mỗi biến thể; trạng thái PUBLISHED).
  - Vợt Victor Thruster Ryuga II (SKU 3U, 4U; giá 3.850.000đ; tồn kho 40 cái; trạng thái PUBLISHED).
  - Giày Yonex Power Cushion 65 Z3 (SKU Size 40, 41, 42; giá 2.650.000đ; tồn kho 30 cái; trạng thái PUBLISHED).
- 3 tài khoản demo cố định (mật khẩu chuẩn: `DemoPass123!@#`):
  - **Admin:** `admin@badminton.local` (Họ tên: "Quản Trị Viên", Role: ADMIN, EmailVerified: true)
  - **Staff:** `staff@badminton.local` (Họ tên: "Nhân Viên Vận Hành", Role: STAFF, EmailVerified: true)
  - **Customer:** `customer@badminton.local` (Họ tên: "Khách Hàng Thân Thiết", Role: CUSTOMER, EmailVerified: true, đã có sẵn 1 địa chỉ mặc định tại TP.HCM)
- Tính idempotent: Dùng `ON CONFLICT DO NOTHING` hoặc `INSERT ... WHERE NOT EXISTS`.

### 3.2 Cấu hình Playwright E2E (`frontend/playwright.config.ts`)
- Target URL: `http://localhost:8080` (Docker Compose Nginx reverse proxy) hoặc `http://localhost:5173` (Local dev server).
- Mailpit API: `http://localhost:8025/api/v1` để truy vấn email outbox thực tế và parse link xác thực.
- Trình duyệt: Chromium / Edge headless.
- Artifacts: Screenshot on failure, video on retry.

### 3.3 GitHub Actions Workflow (`.github/workflows/ci.yml`)
- Triggers: `push: branches: [main]`, `pull_request: branches: [main]`.
- Matrix & Environment:
  - Service: `postgres:18-alpine`
  - Node.js: `22.x`
  - .NET SDK: `10.0.x`

---

## 4. Kế hoạch triển khai theo từng bước

- **Bước 1:** Tạo file seed demo `database/007_demo_seed.sql`, cập nhật `compose.yaml` mount `007_demo_seed.sql` vào init directory.
- **Bước 2:** Rà soát và hoàn thiện bảo mật: security headers, cookie attributes, secret cleanup, cập nhật `.env.example`.
- **Bước 3:** Cấu hình Playwright E2E trong `frontend/` (hoặc root `e2e/`), viết kịch bản E2E test cho 7 luồng cốt lõi.
- **Bước 4:** Xây dựng workflow GitHub Actions `.github/workflows/ci.yml`.
- **Bước 5:** Xây dựng script one-click demo `scripts/demo-up.ps1` & `scripts/demo-up.sh` và tài liệu `docs/operations/release-checklist.md`.
- **Bước 6:** Cập nhật `docs/TASKS.md`, `README.md`, chạy lại toàn bộ test suite và cập nhật Graphify knowledge graph.

