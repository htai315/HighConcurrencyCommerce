# Implementation Plan — T23: Flash Sale Campaign / Offer Management

## 1. Mục tiêu và Tiêu chí hoàn thành
- **Mục tiêu:** Cung cấp đầy đủ tính năng quản trị chiến dịch và ưu đãi Flash Sale cho Admin:
  - Tạo, cập nhật, xem danh sách và chi tiết Flash Sale Campaign.
  - Cấu hình Offers (SKU, giá flash sale, quota, giới hạn mỗi khách hàng).
  - Kiểm tra các ràng buộc nghiệp vụ (SKU ACTIVE, Product PUBLISHED, Giá sale < Giá regular, Quota <= Tồn khả dụng).
  - Công bố (Publish): Chuyển `DRAFT` thành `SCHEDULED` (nếu chưa đến giờ) hoặc `ACTIVE` (nếu đang trong giờ).
  - Hủy chiến dịch (Cancel): Khóa hàng `FOR UPDATE`, chuyển sang `CANCELLED`, chặn mua mới, bảo lưu đơn hàng cũ (SM-Q02), ghi Audit Log.
  - Public API cho khách hàng xem Flash Sale đang hoặc sắp diễn ra.
  - Giao diện Admin quản trị trực quan với đầy đủ chức năng và validation.
  - Tích hợp và đồng bộ kiểm thử 100% BE + FE.

## 2. Các thành phần thực hiện

### 2.1 Backend (Application & Infrastructure)
1. **Application Contracts (`HighConcurrencyCommerce.Application/FlashSale/FlashSaleContracts.cs`):**
   - Request DTOs: `CreateFlashSaleCampaignRequest`, `UpdateFlashSaleCampaignRequest`, `CreateFlashSaleOfferRequest`.
   - Response DTOs: `FlashSaleCampaignDto`, `FlashSaleOfferDto`, `FlashSalePerformanceDto`, `PublicFlashSaleDto`.
   - Service Interface: `IFlashSaleService`.
   - Custom Exception: `FlashSaleFailure`.
2. **Infrastructure Service (`HighConcurrencyCommerce.Infrastructure/FlashSale/PostgresFlashSaleService.cs`):**
   - CRUD và State Transitions với transaction & row locks (`FOR UPDATE`).
   - Validate nghiệp vụ: SKU status, Product publication status, regular price vs sale price, available stock vs quota.
   - Idempotency & Concurrency: Khóa dòng chiến dịch để bảo vệ trước race publish & cancel.
   - Ghi nhận `AuditLog` cho các hành động: `FLASH_SALE_CAMPAIGN_CREATED`, `FLASH_SALE_CAMPAIGN_UPDATED`, `FLASH_SALE_CAMPAIGN_PUBLISHED`, `FLASH_SALE_CAMPAIGN_CANCELLED`, `FLASH_SALE_OFFER_ADDED`, `FLASH_SALE_OFFER_REMOVED`.
3. **API Controllers & Error Handling:**
   - `AdminFlashSaleController.cs` (`[Authorize(Policy = "Admin")]`): Tuyệt đối bảo vệ quyền Admin.
   - `PublicFlashSaleController.cs` / mở rộng `CatalogController.cs` (`[AllowAnonymous]`): Lấy thông tin flash sale cho storefront.
   - `FlashSaleExceptionHandler.cs`: Ánh xạ `FlashSaleFailure` sang RFC 7807 ProblemDetails.
   - Đăng ký DI trong `Program.cs`.

### 2.2 Backend Integration Tests (`HighConcurrencyCommerce.IntegrationTests/FlashSaleCampaignTests.cs`)
1. **RBAC Tests:**
   - Guest hoặc Customer hoặc Staff gọi Admin Flash Sale endpoints nhận 401 hoặc 403.
   - Admin thực hiện thành công.
2. **Validation & Business Invariant Tests:**
   - Tạo campaign với `ends_at <= starts_at` hoặc `ends_at <= now` nhận 400.
   - Thêm offer với giá sale $\ge$ regular price nhận 400.
   - Thêm offer với quota > available stock nhận 400.
   - Thêm offer với SKU không ACTIVE hoặc Product DRAFT nhận 400.
   - Thêm SKU trùng lặp vào cùng 1 campaign nhận 409 CONFLICT.
3. **Lifecycle & State Machine Tests:**
   - Publish campaign không có offer nhận 400.
   - Publish campaign với `starts_at > now` chuyển sang `SCHEDULED`.
   - Publish campaign với `starts_at <= now < ends_at` chuyển sang `ACTIVE`.
   - Cancel campaign DRAFT/SCHEDULED/ACTIVE chuyển sang `CANCELLED`.
   - Cancel lặp lại hoặc cancel campaign đã ENDED nhận 409 INVALID_STATE_TRANSITION.
4. **Concurrency & Locking Tests:**
   - Hai Admin cùng cancel đồng thời hoặc một publish một cancel: chỉ 1 request thành công, không tạo duplicate audit logs.

### 2.3 Frontend Implementation
1. **API Client (`frontend/src/features/flash-sale/flash-sale.api.ts`):**
   - Định nghĩa TypeScript types.
   - Các hàm gọi API Admin và Public kèm CSRF token.
2. **Admin UI (`frontend/src/pages/AdminDashboardPage.tsx` hoặc component con trong `frontend/src/features/flash-sale/AdminFlashSalePanel.tsx`):**
   - Tab "Flash Sale" trong Admin Dashboard.
   - Danh sách campaigns, badge trạng thái (DRAFT, SCHEDULED, ACTIVE, ENDED, CANCELLED).
   - Form tạo campaign mới (name, starts_at, ends_at).
   - Bảng xem chi tiết campaign và quản lý offers (thêm offer, chọn SKU, nhập sale price, quota, per_customer_limit, nút xóa offer).
   - Nút Publish và nút Cancel campaign với modal/confirm dialog.
3. **Frontend Integration Tests (`frontend/src/test/flash-sale.test.tsx`):**
   - Test Admin xem danh sách campaign, tạo campaign, thêm offer, publish và cancel.
   - Test validation lỗi hiển thị trên UI.

## 3. Checklist nghiệm thu
- [ ] Backend build: 0 error, 0 warning.
- [ ] Backend integration tests: 100% pass (tối thiểu 6 tests mới).
- [ ] Frontend tests: 100% pass (thêm test suite cho flash sale).
- [ ] Frontend lint & build: 0 error, 0 warning.
- [ ] Cập nhật `docs/TASKS.md` (Đợt 18).
- [ ] Chạy `python -m graphify update .` tuân thủ `AGENTS.md`.

