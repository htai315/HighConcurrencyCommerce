# Quy trình Kiểm tra và Release Gate (Release Checklist)

Tài liệu này xác định các tiêu chuẩn bắt buộc (Release Gates) để một phiên bản của hệ thống HighConcurrencyCommerce được coi là đủ điều kiện phát hành (production release candidate).

---

## 1. Cổng Kiểm thử Tự động (Automated Test Gates)

Tất cả các lệnh kiểm thử sau phải chạy thành công 100% không có ngoại lệ:

1. **Backend Integration Tests:**
   ```bash
   dotnet test HighConcurrencyCommerce/HighConcurrencyCommerce.IntegrationTests/HighConcurrencyCommerce.IntegrationTests.csproj
   ```
   - Tiêu chí hiện tại: **508/508 tests passed** (bao gồm concurrency, migrations, PayOS reconciliation/refund hardening, malformed webhook validation, Wishlist alerts, advanced search, Product Comparison public projection/category guard, đúng một DB command, timeout/restore và DemoSeed idempotency).
   - xUnit chạy `parallelAlgorithm=conservative`, tối đa bốn collection threads để tránh bão Testcontainers trên máy nhiều core; concurrency bên trong từng acceptance test không bị giảm.

2. **Frontend Static Analysis & Unit/Component Tests:**
   ```bash
   npm run lint --prefix frontend
   npm test --prefix frontend -- --run
   npm run build --prefix frontend
   ```
   - Tiêu chí:
     - `oxlint`: 0 warnings, 0 errors.
     - `vitest`: 162/162 tests passed.
     - Production build: `tsc -b && vite build` hoàn thành không lỗi.

3. **Playwright End-to-End Live Stack Tests:**
   ```bash
   npm run test:e2e --prefix frontend
   ```
   - Tiêu chí: Toàn bộ **20 tests trong 18 specs** được discovery; CI phải tìm thấy `18-product-comparison.spec.ts` trước khi khởi động stack.
   - Với T52, Flow 18 phải xanh trên Docker/PostgreSQL thật: URL share độc lập localStorage, matrix/different-only, category guard và lifecycle omission generic.
   - Regression bắt buộc cho T52: Flow 5, 12, 16 và 17.

4. **Docker Compose Configuration:**
   ```bash
   docker compose config --quiet
   ```
   - Tiêu chí: Không có lỗi cú pháp, biến môi trường bắt buộc đều được khai báo rõ ràng.

---

## 2. Cổng Bảo mật (Security Gates)

1. **Quản lý Secrets & Credentials:**
   - [x] Không lưu Google Client Secret trong mã nguồn, frontend bundle, Git commit hay `.env.example`.
   - [x] Dòng chảy Google Identity Services (GIS) chỉ cần `GoogleAuth:ClientId`. Mọi secret nhạy cảm từng xuất hiện trong quá trình thảo luận phải được thu hồi/xoay vòng (rotate) tại Google Cloud Console.
   - [x] Không hardcode mật khẩu hay chuỗi kết nối trong code C# hay TypeScript.
   - [x] `.env` được cấu hình trong `.gitignore`.

2. **Cookie & Phiên Đăng nhập:**
   - [x] Session cookie có cờ `HttpOnly`, `SameSite=Lax` (hoặc `Strict`), `Secure` khi chạy HTTPS.
   - [x] Anti-CSRF token bắt buộc trên mọi phương thức thay đổi trạng thái (`POST`, `PATCH`, `DELETE`).
   - [x] Thu hồi toàn bộ session khi đổi mật khẩu, reset mật khẩu hoặc khi nhân sự bị khóa tài khoản.

3. **Bảo vệ Token & Email Outbox:**
   - [x] Chỉ lưu hash một chiều (SHA-256) của token trong database; không bao giờ lưu token thô.
   - [x] Raw token trong email dùng URL fragment (`#token=...`), trình duyệt xóa fragment ngay khi load trang.
   - [x] Worker gửi email chạy ngoài database transaction, không giữ lock khi gọi SMTP.
   - [x] Commerce email chỉ dùng template allowlist và internal action path; worker revalidate account, verified email và recipient snapshot trước khi gửi.
   - [x] Wishlist evaluator và commerce email worker giải phóng lease khi shutdown trong tối đa 5 giây; lease 5 phút là recovery fallback.

4. **HTTP Security Headers:**
   - [x] `X-Content-Type-Options: nosniff`
   - [x] `X-Frame-Options: DENY`
   - [x] `Referrer-Policy: strict-origin-when-cross-origin`
   - [x] `Content-Security-Policy`: Chỉ cho phép domain tự thân và Google Identity Services (`accounts.google.com`).
   - [x] `Cache-Control: no-store` cho các endpoint dữ liệu riêng tư và giao dịch.

---

## 3. Cổng Vận hành & Khả năng Phục hồi (Operational Gates)

1. **Database Migration & Idempotency:**
   - Service `migrator` phải exit `0` trước khi API khởi động; `docker compose logs migrator` không có lỗi.
   - `public.schema_migrations` có đủ version 001–021, ngoại trừ 007 là demo seed không thuộc schema history: hiện tại 20 migration schema.
   - Fresh database, legacy upgrade, concurrent runners, checksum drift và failed-migration retry đều được integration test.
   - File migration đã áp dụng không bị sửa; checksum SHA-256 trong database khớp artifact.
   - Dữ liệu demo chỉ được nạp bằng `--seed-demo` khi đồng thời ở môi trường non-production và `DemoSeed:Enabled=true`.
   - Không có migration nào tự động xác minh email của tài khoản chưa xác minh khi chạy lại.

2. **Tài khoản Demo Cố định (Chỉ dùng môi trường Local/Demo):**
   - Admin: `admin@badminton.local` (`DemoPass123!@#`)
   - Staff: `staff@badminton.local` (`DemoPass123!@#`)
   - Customer: `customer@badminton.local` (`DemoPass123!@#`)

3. **Health Check Endpoints & Nginx Proxy:**
   - Liveness probe: `GET /health/live` trả về `200 Healthy`.
   - Readiness probe: `GET /health/ready` xác nhận kết nối PostgreSQL trước khi mở lưu lượng.
   - Nginx cấu hình proxy trực tiếp location `/health/` tới API backend (thay vì fallback vào `index.html`).

4. **Wishlist Product Alert Workers:**
   - `product_alert_refresh_queue` không có lease quá 5 phút sau một chu kỳ recovery.
   - `commerce_email_outbox` không tăng backlog đến hạn liên tục; discarded/error phải được phân loại trước release.
   - Không xóa queue/outbox hoặc sửa `sent_at` thủ công để làm xanh release gate.
   - Dùng [runbook Wishlist Product Alerts](wishlist-product-alerts.md) để kiểm tra SQL read-only, log và quy trình phục hồi.

