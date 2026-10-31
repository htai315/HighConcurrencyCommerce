# Implementation Plan — T24: Flash Sale Checkout Allocation

## 1. Mục tiêu & Tiêu chí hoàn thành
- **Mục tiêu:** Mở rộng transaction Checkout cho phép đặt mua ưu đãi Flash Sale với cơ chế giữ quota (`HELD`), kiểm soát giới hạn mỗi khách hàng (`per_customer_limit`), cập nhật giá Flash Sale, và xử lý chuyển trạng thái vòng đời (`HELD -> SOLD / RELEASED`).
- **Invariants cốt lõi:**
  1. **Thứ tự khóa (Lock Order):**
     `Customer FOR UPDATE -> FlashSaleOffer FOR UPDATE (sort UUID) -> ProductSku FOR UPDATE (sort UUID) -> InventoryBalance FOR UPDATE (sort UUID)`.
     Ngăn chặn tuyệt đối deadlock giữa các request checkout đồng thời.
  2. **Campaign & Offer Guard:**
     - Offer phải thuộc SKU đã chọn.
     - Campaign phải đang `ACTIVE` và trong khung giờ `starts_at <= now < ends_at`.
  3. **Quota Allocation Guard:**
     - `quantity <= offer.quota_total - (offer.quota_held + offer.quota_sold)`.
     - Nếu vượt quota, từ chối với `409 FLASH_SALE_QUOTA_EXCEEDED`.
  4. **Per-Customer Limit Guard:**
     - Tổng số lượng khách hàng đã giữ (`HELD`) hoặc đã mua (`SOLD`) trên các đơn không hủy/không hết hạn + `quantity` mới không được vượt `offer.per_customer_limit`.
     - Nếu vượt giới hạn, từ chối với `409 FLASH_SALE_CUSTOMER_LIMIT_EXCEEDED`.
  5. **Snapshot & Pricing:**
     - `UnitPrice` của `SalesOrderItem` được ghi nhận theo `offer.sale_price`.
     - `FlashSaleAllocation` được tạo ở trạng thái `HELD` liên kết với `SalesOrderItem`.
     - Tồn kho vật lý vẫn được `RESERVE` bình thường trên `InventoryBalance`.
  6. **Vòng đời FlashSaleAllocation:**
     - Khi Staff confirm COD: `HELD -> SOLD`, `QuotaHeld -= qty`, `QuotaSold += qty`.
     - Khi Order hủy hoặc hết hạn: `HELD -> RELEASED`, `QuotaHeld -= qty`.
     - Khi hoàn kho (Return Stock): `SOLD -> RELEASED`, và nếu Campaign vẫn còn `ACTIVE`, hoàn lại `QuotaSold -= qty` (SM-Q06).
  7. **Concurrency & High Load:**
     - Nhiều Customer cùng tranh chấp quota hữu hạn (ví dụ quota = 5, 20 khách tranh chấp): đúng 5 suất được cấp, không overselling, bảo toàn DB check constraint `ck_flash_sale_quota`.

---

## 2. Các bước thực hiện

### 2.1 Backend Implementation
1. **Checkout Service (`PostgresCheckoutService.cs`):**
   - Bỏ chặn `FLASH_SALE_NOT_SUPPORTED` trong `ValidatePreflight`.
   - Cập nhật `ComputePayloadHash` để tính cả `flashSaleOfferId`.
   - Bổ sung khóa `store.flash_sale_offers ... FOR UPDATE` theo thứ tự UUID tăng dần.
   - Thêm logic kiểm tra Campaign status, Quota available, và Per-customer limit.
   - Tạo dòng `FlashSaleAllocation` (`HELD`) và cập nhật `offer.QuotaHeld += qty`.
   - Đặt `unitPrice = offer.SalePrice` cho SalesOrderItem.
2. **Lifecycle Handlers:**
   - [`PostgresCodOrderService.cs`](file:///d:/Personal_Project/HighConcurrencyCommerce/HighConcurrencyCommerce.Infrastructure/Orders/PostgresCodOrderService.cs):
     - `ConfirmOrderAsync`: Chuyển `FlashSaleAllocation` `HELD -> SOLD`, `QuotaHeld -= qty`, `QuotaSold += qty`.
     - `ExpirePendingOrdersAsync`: Chuyển `FlashSaleAllocation` `HELD -> RELEASED`, `QuotaHeld -= qty`.
   - [`PostgresCustomerOrderService.cs`](file:///d:/Personal_Project/HighConcurrencyCommerce/HighConcurrencyCommerce.Infrastructure/CustomerOrders/PostgresCustomerOrderService.cs):
     - `CancelOrderAsync`: Chuyển `FlashSaleAllocation` `HELD -> RELEASED`, `QuotaHeld -= qty`.
   - [`PostgresFulfillmentService.cs`](file:///d:/Personal_Project/HighConcurrencyCommerce/HighConcurrencyCommerce.Infrastructure/Fulfillment/PostgresFulfillmentService.cs):
     - `ProcessReturnStockAsync`: Chuyển `FlashSaleAllocation` `SOLD -> RELEASED`, nếu Campaign còn `ACTIVE` thì `QuotaSold -= qty`.

### 2.2 Frontend Client Update
- Cập nhật [`checkout.api.ts`](file:///d:/Personal_Project/frontend/src/features/checkout/checkout.api.ts):
  - `CheckoutLine` hỗ trợ `flashSaleOfferId?: string | null`.
  - Giữ nguyên `flashSaleOfferId` khi gửi request thay vì ép null.

### 2.3 Backend Integration Tests (`FlashSaleCheckoutTests.cs`)
1. **Flash Sale Checkout Success:**
   - Checkout thành công với giá Flash Sale.
   - Tạo `FlashSaleAllocation` `HELD`, `QuotaHeld` tăng, `unit_price` đúng `SalePrice`.
2. **Quota Exceeded Rejection:**
   - Mua quá quota còn lại nhận `409 FLASH_SALE_QUOTA_EXCEEDED`.
3. **Per-Customer Limit Rejection:**
   - Khách mua vượt `per_customer_limit` nhận `409 FLASH_SALE_CUSTOMER_LIMIT_EXCEEDED`.
4. **Inactive Campaign Rejection:**
   - Mua offer của chiến dịch chưa ACTIVE hoặc đã bị CANCELLED nhận `409 FLASH_SALE_CAMPAIGN_NOT_ACTIVE`.
5. **Full Lifecycle Allocation Transitions:**
   - Checkout `HELD` -> Staff Confirm `SOLD` -> QuotaSold tăng, QuotaHeld giảm.
   - Checkout `HELD` -> Customer Cancel `RELEASED` -> QuotaHeld giảm.
   - Checkout `HELD` -> Worker Expiry `RELEASED` -> QuotaHeld giảm.
6. **High Concurrency Quota Race Test:**
   - 20 khách hàng cùng checkout cạnh tranh quota 5 của một Flash Sale SKU.
   - Đúng 5 request thành công, 15 request nhận `FLASH_SALE_QUOTA_EXCEEDED`.
   - Không oversell, tổng quota held = 5, DB check constraint thỏa mãn 100%.

---

## 3. Checklist nghiệm thu
- [ ] Backend build: 0 error, 0 warning.
- [ ] Backend integration tests: 100% pass (toàn bộ test cũ + test Flash Sale checkout mới).
- [ ] Frontend tests: 100% pass.
- [ ] Frontend lint & build: 0 error, 0 warning.
- [ ] Cập nhật `docs/api/checkout.md`, `docs/TASKS.md`, `README.md`.
- [ ] Chạy `python -m graphify update .` tuân thủ `AGENTS.md`.

