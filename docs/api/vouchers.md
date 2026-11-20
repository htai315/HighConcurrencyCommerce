# Basic Vouchers API Specification (T38)

Tài liệu đặc tả các giao diện API quản lý và sử dụng Voucher (Mã giảm giá đơn hàng) trong hệ thống High-Concurrency E-Commerce.

---

## 1. Tổng quan Nghiệp vụ

- **Loại chiết khấu (`discount_type`)**:
  - `FIXED_AMOUNT`: Chiết khấu số tiền cố định (VNĐ, số nguyên dương, không có trần).
  - `PERCENTAGE`: Chiết khấu phần trăm (1% - 99%, bắt buộc có `max_discount_amount`).
- **Nguyên tắc áp dụng**:
  - Tối đa 1 voucher cho mỗi đơn hàng (chống coupon stacking).
  - Áp dụng trên `subtotal` sau khi đã tính giá ưu đãi Flash Sale.
  - Kiểm tra điều kiện giá trị đơn hàng tối thiểu `minimum_order_amount`.
  - Giới hạn tổng lượt sử dụng (`total_usage_limit`), giới hạn mỗi khách (`per_customer_limit`) và tổng ngân sách tài trợ (`budget_total`).
- **Vòng đời trạng thái Voucher**:
  - `DRAFT` (Bản nháp) -> `ACTIVE` (Đang mở) <-> `PAUSED` (Tạm dừng) -> `ENDED` (Kết thúc vĩnh viễn).
- **Vòng đời trạng thái Redemption**:
  - `HELD`: Giữ tạm thời tại thời điểm checkout đơn hàng (khóa đồng thời và trừ quota nguyên tử).
  - `CONSUMED`: Tiêu thụ chính thức khi đơn COD hoàn tất giao hàng và đã thu tiền thành công.
  - `RELEASED`: Hoàn trả quota khi đơn bị hủy bởi khách, hết hạn xác nhận COD hoặc giao thất bại đã hoàn kho.
- **Thứ tự khóa tài nguyên (Lock Ordering chống deadlock)**:
  1. `Customer (store.user_accounts)`
  2. `Voucher (store.vouchers)`
  3. `Flash Sale Campaign (store.flash_sale_campaigns)`
  4. `SKUs (store.product_skus theo thứ tự Id tăng dần)`
  5. `Inventory Balances (store.inventory_balances theo thứ tự Id tăng dần)`

---

## 2. Customer Vouchers API

### 2.1. Preview Voucher (`POST /api/v1/vouchers/preview`)
Kiểm tra tính hợp lệ và xem trước số tiền giảm giá cho giỏ hàng hiện tại của khách hàng mà không giữ quota hay khóa voucher.

- **Quyền hạn**: Authenticated Customer.
- **CSRF**: Bắt buộc header `X-CSRF-TOKEN`.
- **Request Body**:
```json
{
  "code": "GIAM50K"
}
```
- **Response 200 OK**:
```json
{
  "code": "GIAM50K",
  "subtotalAmount": 1000000.0,
  "discountAmount": 50000.0,
  "totalAmount": 950000.0,
  "validUntil": "2026-11-30T23:59:59Z"
}
```
- **Error Codes**:
  - `400 VOUCHER_NOT_FOUND`: Mã không tồn tại hoặc đã bị xóa.
  - `400 VOUCHER_NOT_ACTIVE`: Mã chưa được kích hoạt hoặc đang tạm dừng.
  - `400 VOUCHER_NOT_STARTED`: Mã chưa đến thời gian áp dụng.
  - `400 VOUCHER_EXPIRED`: Mã đã hết hạn áp dụng.
  - `400 VOUCHER_MINIMUM_NOT_MET`: Giá trị đơn hàng chưa đạt mức tối thiểu.
  - `400 VOUCHER_USAGE_LIMIT_REACHED`: Mã đã hết lượt sử dụng.
  - `400 VOUCHER_CUSTOMER_LIMIT_REACHED`: Khách hàng đã đạt giới hạn sử dụng mã.
  - `400 VOUCHER_BUDGET_EXHAUSTED`: Ngân sách tài trợ của mã đã hết.
  - `400 EMPTY_CART`: Giỏ hàng đang trống.

---

## 3. Checkout Allocation API (`POST /api/v1/checkout`)

Khi gửi kèm voucher, checkout chuyển sang flow `checkout-v2` với nguyên tử hóa phân bổ `HELD`:

- **Request Body**:
```json
{
  "addressId": "00000000-0000-0000-0000-000000000001",
  "paymentMethod": "COD",
  "voucherCode": "GIAM50K",
  "lines": [
    {
      "productSkuId": "00000000-0000-0000-0000-000000000002",
      "quantity": 1,
      "flashSaleOfferId": null
    }
  ]
}
```
- **Headers**:
  - `Idempotency-Key`: `checkout-v2-{requestId}` (giữ nguyên nếu không dùng voucher là `checkout-v1-{requestId}`).
  - `X-CSRF-TOKEN`: CSRF Token hợp lệ.
- **Response 200 OK**:
```json
{
  "orderId": "00000000-0000-0000-0000-000000000010",
  "orderNumber": "ORD-20261105-001",
  "status": "PENDING_COD_CONFIRMATION",
  "subtotalAmount": 1000000.0,
  "voucherDiscountAmount": 50000.0,
  "voucherCode": "GIAM50K",
  "totalAmount": 950000.0,
  "createdAt": "2026-11-05T00:00:00Z"
}
```

---

## 4. Admin Vouchers Management API

Toàn bộ API dưới đây yêu cầu quyền `ADMIN` và header `X-CSRF-TOKEN` cho các mutation requests (`POST`, `PATCH`).

### 4.1. Danh sách Voucher (`GET /api/v1/admin/vouchers`)
- **Query Params**:
  - `page`: Trang hiện tại (1 - 100,000, mặc định 1).
  - `pageSize`: Số mục mỗi trang (1 - 100, mặc định 20).
  - `status`: Lọc theo trạng thái (`DRAFT`, `ACTIVE`, `PAUSED`, `ENDED`).
  - `q`: Tìm kiếm từ khóa theo mã (`code`) hoặc tên chương trình (`name`) (không phân biệt hoa thường).
- **Response 200 OK**:
```json
{
  "items": [
    {
      "id": "00000000-0000-0000-0000-000000000001",
      "code": "GIAM50K",
      "name": "Khuyến mãi ra mắt",
      "voucherStatus": "ACTIVE",
      "discountType": "FIXED_AMOUNT",
      "discountValue": 50000.0,
      "maxDiscountAmount": null,
      "minimumOrderAmount": 200000.0,
      "totalUsageLimit": 500,
      "perCustomerLimit": 1,
      "usageHeld": 5,
      "usageConsumed": 120,
      "remainingUsage": 375,
      "budgetTotal": 25000000.0,
      "budgetHeld": 250000.0,
      "budgetConsumed": 6000000.0,
      "remainingBudget": 18750000.0,
      "startsAt": "2026-11-01T00:00:00Z",
      "endsAt": "2026-11-30T23:59:59Z",
      "activatedAt": "2026-11-01T00:00:00Z",
      "createdAt": "2026-10-31T00:00:00Z",
      "updatedAt": "2026-11-01T00:00:00Z"
    }
  ],
  "totalCount": 1,
  "page": 1,
  "pageSize": 20
}
```

### 4.2. Tạo Voucher Mới (`POST /api/v1/admin/vouchers`)
Tạo voucher mới ở trạng thái `DRAFT` và ghi lại Audit Log (`ACTION = CREATE_VOUCHER`).
- Bắt buộc kiểm tra trùng mã đồng thời: nếu vi phạm unique index, hệ thống trả mã lỗi `409 CONFLICT` an toàn thay vì `500`.
- **Request Body**:
```json
{
  "code": "GIAM50K",
  "name": "Khuyến mãi ra mắt",
  "discountType": "FIXED_AMOUNT",
  "discountValue": 50000,
  "minimumOrderAmount": 200000,
  "maxDiscountAmount": null,
  "totalUsageLimit": 500,
  "perCustomerLimit": 1,
  "budgetTotal": 25000000,
  "startsAt": "2026-11-01T00:00:00Z",
  "endsAt": "2026-11-30T23:59:59Z"
}
```

### 4.3. Chỉnh sửa Bản nháp (`PATCH /api/v1/admin/vouchers/{id}`)
Chỉ được chỉnh sửa khi voucher đang ở trạng thái `DRAFT`.
- **Request Body**: Tương tự `POST /api/v1/admin/vouchers` (bắt buộc bao gồm `code` và hỗ trợ `name`).

### 4.4. Chuyển đổi Trạng thái Vòng đời
- **Kích hoạt**: `POST /api/v1/admin/vouchers/{id}/activate` (`DRAFT -> ACTIVE`).
- **Tạm dừng**: `POST /api/v1/admin/vouchers/{id}/pause` (`ACTIVE -> PAUSED`).
- **Mở lại**: `POST /api/v1/admin/vouchers/{id}/resume` (`PAUSED -> ACTIVE`).
- **Kết thúc vĩnh viễn**: `POST /api/v1/admin/vouchers/{id}/end` (`ACTIVE/PAUSED -> ENDED`).
Mọi thay đổi trạng thái đều ghi Audit Log `store.audit_logs` đầy đủ thông tin `actor_id`, `before_state`, `after_state`.

