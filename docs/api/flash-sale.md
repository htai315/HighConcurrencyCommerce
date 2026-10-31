# Flash Sale Campaign & Offer API Specification

Tài liệu này đặc tả API quản trị chiến dịch và ưu đãi Flash Sale (T23) theo kiến trúc của HighConcurrencyCommerce.

---

## 1. Nguyên tắc thiết kế & Invariants

1. **RBAC:**
   - Chỉ `ADMIN` được tạo, sửa, công bố, hủy chiến dịch Flash Sale và quản lý danh sách offers.
   - Staff, Customer hoặc Guest gọi các endpoint admin sẽ nhận `401 Unauthorized` hoặc `403 Forbidden`.
   - Khách vãng lai và mọi role đều có thể xem danh sách chiến dịch đang diễn ra (`ACTIVE`) hoặc sắp diễn ra (`SCHEDULED`) qua Public API.

2. **Vòng đời Campaign (`campaign_status`):**
   - `DRAFT`: Mới tạo, cho phép sửa thông tin, thêm/xóa offer.
   - `SCHEDULED`: Đã được duyệt publish nhưng `now < starts_at`.
   - `ACTIVE`: Đang trong khung giờ mở bán (`starts_at <= now < ends_at`).
   - `ENDED`: Hết giờ mở bán (`now >= ends_at`).
   - `CANCELLED`: Admin chủ động hủy.
   - Hosted lifecycle worker lưu bền các bước `SCHEDULED -> ACTIVE -> ENDED`. Public API và checkout đồng thời đánh giá theo `starts_at/ends_at`, nên polling delay không tạo khoảng thời gian bán sai.

3. **SM-Q02 — Cancel Flash Sale ACTIVE:**
   - Admin hủy chiến dịch chuyển trạng thái sang `CANCELLED`.
   - **Chỉ dừng mua mới:** các request checkout mới sẽ bị từ chối do campaign không còn `ACTIVE`.
   - **Không hồi tố:** các đơn hàng đã tạo, đã giữ chỗ (`HELD`) hoặc đã thanh toán (`PAID`/`CONFIRMED`) vẫn giữ nguyên giá và snapshot tại thời điểm checkout.

4. **Invariants cho Offer:**
   - SKU phải thuộc sản phẩm đã `PUBLISHED` và trạng thái SKU là `ACTIVE`.
   - `sale_price > 0` và `sale_price < regular_price` hiện tại của SKU.
   - `quota_total > 0` và tại thời điểm cấu hình/công bố không vượt quá tồn khả dụng (`available_qty`) của kho.
   - `per_customer_limit >= 1` (mặc định 2).
   - Mỗi SKU chỉ xuất hiện tối đa một lần trong cùng một campaign (`UNIQUE(campaign_id, product_sku_id)`).

5. **Concurrency & Locking:**
   - Mọi thao tác chuyển trạng thái (Publish, Cancel) đều mở transaction và khóa dòng `store.flash_sale_campaigns` bằng `FOR UPDATE`.
   - Khi công bố chiến dịch, khóa thêm các dòng `store.product_skus` và `store.inventory_balances` liên quan để đảm bảo tính toàn vẹn của giá và tồn kho.
   - Thao tác cancel đồng thời chỉ một request thành công cập nhật trạng thái và ghi Audit Log duy nhất; request thứ hai nhận `409 INVALID_STATE_TRANSITION`.
   - Checkout khóa theo thứ tự Campaign → Offer → SKU → InventoryBalance. Cancel và checkout khóa cùng campaign, loại bỏ stale-read giữa hai transaction.

6. **Thanh toán:** Flash Sale hiện hỗ trợ COD theo [ADR 0004](../adr/0004-enable-cod-for-flash-sale-while-payos-deferred.md). PayOS vẫn deferred theo ADR 0003.

---

## 2. Admin Endpoints (`[Authorize(Policy = "Admin")]`)

### 2.1 Lấy danh sách chiến dịch
- **Method:** `GET`
- **Path:** `/api/v1/admin/flash-sales`
- **Response (200 OK):**
```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "name": "Flash Sale Giờ Vàng 11.11",
    "startsAt": "2026-11-11T00:00:00Z",
    "endsAt": "2026-11-11T02:00:00Z",
    "campaignStatus": "DRAFT",
    "createdAt": "2026-10-30T10:00:00Z",
    "updatedAt": "2026-10-30T10:00:00Z",
    "offersCount": 2,
    "totalQuota": 50,
    "totalSold": 0
  }
]
```

### 2.2 Xem chi tiết chiến dịch
- **Method:** `GET`
- **Path:** `/api/v1/admin/flash-sales/{id}`
- **Response (200 OK):**
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "Flash Sale Giờ Vàng 11.11",
  "startsAt": "2026-11-11T00:00:00Z",
  "endsAt": "2026-11-11T02:00:00Z",
  "campaignStatus": "DRAFT",
  "createdAt": "2026-10-30T10:00:00Z",
  "updatedAt": "2026-10-30T10:00:00Z",
  "offers": [
    {
      "id": "7ca85f64-5717-4562-b3fc-2c963f66afa1",
      "campaignId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "productSkuId": "1ca85f64-5717-4562-b3fc-2c963f66afa2",
      "productTitle": "Bàn phím cơ HCC Pro",
      "skuCode": "HCC-KEY-01",
      "optionLabel": "Đen / Red Switch",
      "regularPrice": 1200000.00,
      "salePrice": 790000.00,
      "quotaTotal": 30,
      "quotaHeld": 0,
      "quotaSold": 0,
      "perCustomerLimit": 2
    }
  ]
}
```

### 2.3 Tạo chiến dịch mới (DRAFT)
- **Method:** `POST`
- **Path:** `/api/v1/admin/flash-sales`
- **Request Body:**
```json
{
  "name": "Flash Sale Giờ Vàng 11.11",
  "startsAt": "2026-11-11T00:00:00Z",
  "endsAt": "2026-11-11T02:00:00Z"
}
```
- **Validation:**
  - `name`: bắt buộc, 1 đến 240 ký tự.
  - `endsAt > startsAt`.
  - `endsAt > DateTime.UtcNow`.
- **Response (201 Created):** Campaign detail DTO.

### 2.4 Cập nhật thông tin chiến dịch
- **Method:** `PATCH`
- **Path:** `/api/v1/admin/flash-sales/{id}`
- **Điều kiện:** Campaign phải đang ở trạng thái `DRAFT`.
- **Request Body:**
```json
{
  "name": "Flash Sale Giờ Vàng 11.11 (Cập nhật)",
  "startsAt": "2026-11-11T01:00:00Z",
  "endsAt": "2026-11-11T03:00:00Z"
}
```
- **Response (200 OK):** Campaign detail DTO.

### 2.5 Thêm offer vào chiến dịch
- **Method:** `POST`
- **Path:** `/api/v1/admin/flash-sales/{id}/offers`
- **Điều kiện:** Campaign phải đang ở trạng thái `DRAFT`.
- **Request Body:**
```json
{
  "productSkuId": "1ca85f64-5717-4562-b3fc-2c963f66afa2",
  "salePrice": 790000.00,
  "quotaTotal": 30,
  "perCustomerLimit": 2
}
```
- **Validation:**
  - `productSkuId`: SKU phải tồn tại, ACTIVE và Product PUBLISHED.
  - SKU chưa được thêm vào campaign này.
  - `salePrice > 0` và `< regularPrice`.
  - `quotaTotal > 0` và `<= available_qty`.
  - `perCustomerLimit >= 1`.
- **Response (201 Created):** Offer detail DTO.

### 2.6 Xóa offer khỏi chiến dịch
- **Method:** `DELETE`
- **Path:** `/api/v1/admin/flash-sales/{campaignId}/offers/{offerId}`
- **Điều kiện:** Campaign phải đang ở trạng thái `DRAFT`.
- **Response (204 No Content)**.

### 2.7 Công bố / Kích hoạt chiến dịch (Publish)
- **Method:** `POST`
- **Path:** `/api/v1/admin/flash-sales/{id}/publish`
- **Điều kiện:**
  - Campaign đang ở trạng thái `DRAFT`.
  - Có ít nhất 1 offer.
  - Tất cả SKU vẫn ACTIVE, giá regular > sale price, và available_qty >= quota_total.
  - `endsAt > UtcNow`.
- **Kết quả trạng thái:**
  - Nếu `UtcNow < startsAt`: chuyển sang `SCHEDULED`.
  - Nếu `startsAt <= UtcNow < endsAt`: chuyển sang `ACTIVE`.
- **Response (200 OK):**
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "campaignStatus": "SCHEDULED",
  "startsAt": "2026-11-11T00:00:00Z",
  "endsAt": "2026-11-11T02:00:00Z"
}
```

### 2.8 Hủy chiến dịch (Cancel)
- **Method:** `POST`
- **Path:** `/api/v1/admin/flash-sales/{id}/cancel`
- **Request Body (tuỳ chọn):**
```json
{
  "reason": "Điều chỉnh kế hoạch kinh doanh"
}
```
- **Điều kiện:**
  - Campaign đang ở trạng thái `DRAFT`, `SCHEDULED`, hoặc `ACTIVE`.
  - Nếu đã `CANCELLED` hoặc `ENDED`, trả `409 INVALID_STATE_TRANSITION`.
- **Hệ quả:**
  - Chuyển `campaign_status` thành `CANCELLED`.
  - Ghi AuditLog `FLASH_SALE_CAMPAIGN_CANCELLED`.
  - Dừng tức thì mọi lượt mua flash sale mới. Đơn hàng trước đó không bị ảnh hưởng.
- **Response (200 OK):** Campaign detail DTO.

### 2.9 Xem hiệu suất chiến dịch
- **Method:** `GET`
- **Path:** `/api/v1/admin/flash-sales/{id}/performance`
- **Response (200 OK):**
```json
{
  "campaignId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "Flash Sale Giờ Vàng 11.11",
  "status": "ACTIVE",
  "startsAt": "2026-11-11T00:00:00Z",
  "endsAt": "2026-11-11T02:00:00Z",
  "totalQuota": 50,
  "totalHeld": 5,
  "totalSold": 20,
  "remainingQuota": 25,
  "estimatedRevenue": 15800000.00,
  "offers": [
    {
      "offerId": "7ca85f64-5717-4562-b3fc-2c963f66afa1",
      "skuCode": "HCC-KEY-01",
      "productTitle": "Bàn phím cơ HCC Pro",
      "salePrice": 790000.00,
      "quotaTotal": 30,
      "quotaHeld": 3,
      "quotaSold": 15,
      "remainingQuota": 12,
      "soldRevenue": 11850000.00
    }
  ]
}
```

---

## 3. Public Endpoints (`[AllowAnonymous]`)

### 3.1 Danh sách Flash Sale công khai
- **Method:** `GET`
- **Path:** `/api/v1/catalog/flash-sales`
- **Quy tắc:** Chỉ trả về các campaign đang `ACTIVE` hoặc `SCHEDULED` (chưa hết hạn `ends_at > UtcNow`).
- **Response (200 OK):** Danh sách public campaign kèm offers. Mỗi offer trả `quotaTotal` và `remainingQuota` để storefront tính tiến độ đã bán; không trả quota held/sold nội bộ.

### 3.2 Chi tiết Flash Sale công khai
- **Method:** `GET`
- **Path:** `/api/v1/catalog/flash-sales/{id}`
- **Response (200 OK):** Chi tiết 1 campaign công khai và các offer đang mở bán.

