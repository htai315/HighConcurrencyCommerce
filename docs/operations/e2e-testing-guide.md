# E2E Testing Guide (Playwright & Mailpit)

Tài liệu hướng dẫn thực thi và bảo trì bộ kiểm thử End-to-End (E2E) tự động cho hệ thống HighConcurrencyCommerce.

---

## 1. Tổng quan bộ kiểm thử

Hệ thống E2E sử dụng **Playwright** tích hợp với **Mailpit API** để kiểm thử trọn vẹn hành trình người dùng trên trình duyệt thực tế, không dùng mock cho backend hay mail server:

| Spec File | Mục tiêu & Luồng kiểm thử |
| :--- | :--- |
| [`01-auth-lifecycle.spec.ts`](file:///D:/Personal_Project/frontend/e2e/01-auth-lifecycle.spec.ts) | 1. Đăng ký tài khoản Customer $\to$ nhận email qua Mailpit $\to$ click link `#token=...` xác minh email $\to$ đăng nhập.<br>2. Quên mật khẩu $\to$ nhận email reset $\to$ đặt mật khẩu mới $\to$ đăng nhập lại.<br>3. Google Sign-In smoke: Nút đăng nhập Google hiển thị an toàn, không rò rỉ secret. |
| [`02-staff-invitation.spec.ts`](file:///D:/Personal_Project/frontend/e2e/02-staff-invitation.spec.ts) | Admin đăng nhập $\to$ mời Staff mới qua form quản trị $\to$ Staff nhận email kích hoạt $\to$ tự đặt mật khẩu ban đầu $\to$ đăng nhập vào trang `/staff`. |
| [`03-commerce-fulfillment.spec.ts`](file:///D:/Personal_Project/frontend/e2e/03-commerce-fulfillment.spec.ts) | 1. Customer duyệt sản phẩm $\to$ chọn SKU $\to$ thêm vào giỏ $\to$ đặt hàng COD.<br>2. Staff đăng nhập $\to$ xác nhận COD $\to$ đóng gói (PACKED) $\to$ xuất kho (HANDOVER_TO_SHIPPING) $\to$ giao thành công & thu tiền (DELIVERED).<br>3. Customer kiểm tra trạng thái đơn và nhận thông báo in-app. |
| `04-product-reviews.spec.ts` | Mua hàng, giao thành công, đánh giá verified purchase, sửa và kiểm duyệt nội dung. |
| `05-wishlist.spec.ts` | Guest redirect, lưu/bỏ lưu sản phẩm, chọn SKU và chuyển từ wishlist sang giỏ. |
| `06-vouchers.spec.ts` | Admin kích hoạt voucher, Customer checkout, kiểm tra quota và consume sau fulfillment. |
| `07-cod-returns-refunds.spec.ts` | Trả hàng COD, quarantine, kiểm định, hoàn tiền thủ công và reporting. |
| `08-direct-product-exchange.spec.ts` | Đổi trực tiếp sang SKU khác và giao kiện thay thế. |
| `09-category-management.spec.ts` | Vòng đời danh mục và tác động đến taxonomy Storefront. |
| `10-product-media-upload.spec.ts` | Upload media thật, kiểm tra gallery và xóa an toàn. |
| `11-product-revisions.spec.ts` | Staff tạo revision và Storefront chỉ đổi sau Admin approve. |
| `12-product-lifecycle.spec.ts` | Suspend/resume Product trong khi Staff vẫn soạn revision. |
| `13-product-revision-hardening.spec.ts` | Review diff có cấu trúc và durable cleanup media bị từ chối. |
| `14-shipping-methods-fees.spec.ts` | Admin cấu hình Standard/Express và phụ phí; Customer quote/checkout; snapshot bất biến; stale quote bị chặn; Staff thu COD đúng tổng có phí ship. |
| `15-carrier-consignments-tracking.spec.ts` | Admin tạo/activate carrier; Staff tạo vận đơn, tải nhãn thật, bàn giao và ghi timeline; Customer xem tracking HTTPS an toàn; kiểm tra cả nhánh giao thất bại → giao lại → hoàn tất. |
| `16-advanced-product-search.spec.ts` | Search không dấu; autocomplete exact SKU bằng bàn phím; category/brand/stock/price/rating; sort và URL back/forward; mobile drawer focus/Escape; suspend/resume không rò Product qua result/suggestion. |
| `17-wishlist-product-alerts.spec.ts` | Tạo Product hết hàng cô lập; Customer bật back-in-stock + regular-price-drop và email; Staff stock-in, Admin giảm giá; kiểm tra in-app/Mailpit exactly-once, reload không nhân bản và opt-out không phát thêm. |
| `18-product-comparison.spec.ts` | Guest chọn hai vợt, reload và share URL sang context mới; kiểm tra matrix/different-only/remove-add; chặn giày khác danh mục; Admin suspend và comparison chỉ báo unavailable generic rồi khôi phục Product. |
| `19-related-recent-products.spec.ts` | Guest xem hai vợt, reload kiểm tra persistence và order desc; Storefront hydrate giá/tồn từ API; PDP vợt A gợi ý vợt B và chặn giày khác danh mục; context mới không có history (no cookie/account sync); xóa lịch sử làm section biến mất; Admin suspend và discovery omit generic không rò rỉ trạng thái. |

---

## 2. Yêu cầu môi trường

- **Node.js**: v20+ hoặc v22+
- **Docker & Docker Compose**: Để chạy toàn bộ cụm dịch vụ cục bộ (PostgreSQL, API, Frontend, Mailpit)
- **Playwright Chromium**: Cài đặt qua lệnh `npx playwright install chromium`

---

## 3. Khởi động môi trường kiểm thử

### Cách 1: Sử dụng Docker Compose (Khuyến nghị cho Release Gate & CI)

Khởi động toàn bộ stack với dữ liệu demo:

```bash
# Windows PowerShell
.\scripts\demo-up.ps1

# Linux / macOS Bash
chmod +x ./scripts/demo-up.sh
./scripts/demo-up.sh
```

Hoặc trực tiếp bằng Compose:

```bash
docker compose -f compose.yaml -f compose.demo.yaml up -d --build
docker compose -f compose.yaml -f compose.demo.yaml exec -T api dotnet HighConcurrencyCommerce.Api.dll --seed-demo
```

Kiểm tra trạng thái các container:
- Frontend: `http://localhost:8080`
- API Backend Health: `http://localhost:8080/health/ready`
- Mailpit Web UI: `http://localhost:8025`
- Mailpit API: `http://localhost:8025/api/v1/messages`

---

## 4. Chạy kiểm thử Playwright

Chuyển vào thư mục `frontend`:

```bash
cd frontend
```

### 4.1. Cài đặt browser binaries (chỉ cần chạy lần đầu)
```bash
npx playwright install chromium
```

### 4.2. Chạy toàn bộ test E2E (headless)
```bash
npm run test:e2e
```

### 4.3. Chạy với giao diện Playwright UI (Interactive Mode)
Rất thuận tiện để debug, xem từng bước DOM, network calls và time-travel:
```bash
npx playwright test --ui
```

### 4.4. Chạy một spec file cụ thể
```bash
npx playwright test e2e/01-auth-lifecycle.spec.ts --headed
```

Ví dụ release gate T48:

```bash
npx playwright test e2e/14-shipping-methods-fees.spec.ts
npx playwright test e2e/03-commerce-fulfillment.spec.ts e2e/06-vouchers.spec.ts e2e/07-cod-returns-refunds.spec.ts e2e/11-product-revisions.spec.ts e2e/12-product-lifecycle.spec.ts e2e/13-product-revision-hardening.spec.ts
```

Ví dụ release gate T49:

```bash
npx playwright test e2e/15-carrier-consignments-tracking.spec.ts
npx playwright test e2e/03-commerce-fulfillment.spec.ts e2e/07-cod-returns-refunds.spec.ts e2e/08-direct-product-exchange.spec.ts e2e/14-shipping-methods-fees.spec.ts
```

Ví dụ release gate T50:

```bash
npx playwright test e2e/16-advanced-product-search.spec.ts
npx playwright test e2e/04-product-reviews.spec.ts e2e/05-wishlist.spec.ts e2e/11-product-revisions.spec.ts e2e/12-product-lifecycle.spec.ts e2e/13-product-revision-hardening.spec.ts e2e/14-shipping-methods-fees.spec.ts
```

Ví dụ release gate T51:

```bash
npx playwright test e2e/17-wishlist-product-alerts.spec.ts
npx playwright test e2e/01-auth-lifecycle.spec.ts e2e/05-wishlist.spec.ts e2e/08-direct-product-exchange.spec.ts e2e/11-product-revisions.spec.ts e2e/12-product-lifecycle.spec.ts e2e/16-advanced-product-search.spec.ts
```

Flow 17 xóa mailbox Mailpit trước khi chạy và tạo Customer/Product/SKU riêng theo timestamp. Không đổi dữ liệu tài khoản Customer demo. Flow 8 phải dùng Product fixture `b0000000-0000-0000-0000-000000000001` có hai SKU để kết quả không phụ thuộc Product mới nhất trên Catalog.

Ví dụ release gate T52:

```bash
npx playwright test e2e/18-product-comparison.spec.ts
npx playwright test e2e/05-wishlist.spec.ts e2e/12-product-lifecycle.spec.ts e2e/16-advanced-product-search.spec.ts e2e/17-wishlist-product-alerts.spec.ts
```

Flow 18 dùng ba Product demo cố định (hai vợt cùng danh mục và một đôi giày khác danh mục), chạy với Guest/localStorage riêng và luôn mở bán lại Product trong `finally` nếu bước lifecycle đã suspend thành công.

### 4.5. Xem báo cáo kết quả HTML
```bash
npx playwright show-report
```

---

## 5. Tương tác với Mailpit trong Test

Bộ helper [`mailpit.ts`](file:///D:/Personal_Project/frontend/e2e/helpers/mailpit.ts) cung cấp các tiện ích giao tiếp với REST API của Mailpit:

```typescript
import { waitForEmail, waitForEmailCount, listEmails, extractFragmentTokenFromText, clearAllEmails } from './helpers/mailpit';

// 1. Dọn sạch hộp thư trước test (nếu cần)
await clearAllEmails();

// 2. Chờ email gửi đến địa chỉ người nhận (polling tự động tối đa 15s)
const message = await waitForEmail(recipientEmail, { subject: 'Xác minh địa chỉ email' });

// 3. Trích xuất token từ fragment URL (#token=...) hoặc query (?token=...)
const token = extractFragmentTokenFromText(message.Text || message.HTML);

// 4. Mở trực tiếp trang xác minh trên trình duyệt
await page.goto(`/verify-email#token=${token}`);

// 5. Chứng minh email business event không bị gửi trùng
const messages = await waitForEmailCount(recipientEmail, 1, {
  subject: 'Sản phẩm yêu thích đã có hàng',
});
expect(messages).toHaveLength(1);

// 6. Kiểm tra không có email mới sau khi Customer opt-out
const afterOptOut = await listEmails(recipientEmail, {
  subject: 'Sản phẩm yêu thích đã có hàng',
});
expect(afterOptOut).toHaveLength(1);
```

---

## 6. Xử lý sự cố thường gặp (Troubleshooting)

1. **Test fail do không kết nối được Mailpit (`ECONNREFUSED 127.0.0.1:8025`)**:
   - Kiểm tra container Mailpit có đang chạy không: `docker compose ps mailpit`.
   - Đảm bảo port `8025` không bị chiếm bởi ứng dụng khác.

2. **Test fail do timeout tìm element trên Frontend (`http://localhost:8080`)**:
   - Mở trình duyệt kiểm tra thủ công xem giao diện đã load đầy đủ chưa.
   - Nếu chạy trên máy cấu hình yếu, có thể tăng timeout trong `playwright.config.ts` hoặc truyền flag `--timeout=60000`.

3. **Database bị dirty sau nhiều lần chạy test**:
   - Dữ liệu demo seed luôn có thể reset lại bằng cách:
     ```bash
     docker compose down -v
     docker compose up -d --build
     ```
   - Tất cả tài khoản demo (`admin@badminton.local`, `staff@badminton.local`, `customer@badminton.local`) sẽ được khôi phục về trạng thái chuẩn với mật khẩu `DemoPass123!@#`.

