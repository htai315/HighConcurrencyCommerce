# Shipping Methods & Fees API (T48)

## Quy tắc tiền và báo giá

- Tiền dùng VND nguyên, không âm.
- `shippingFee = baseFee + provinceSurcharge`, hoặc `0` khi subtotal hàng hóa đạt ngưỡng miễn phí.
- Subtotal xét miễn phí đã gồm giá Flash Sale nhưng chưa trừ voucher.
- Tổng đơn: `subtotalAmount - voucherDiscountAmount + shippingFee`.
- Province key được chuẩn hóa bỏ dấu, prefix hành chính và khoảng trắng, ví dụ `Thành phố Hồ Chí Minh` thành `HO CHI MINH`.
- Quote không giữ tồn kho/quota. Checkout phải xác minh lại method revision và phí.

## Admin API

Tất cả endpoint yêu cầu session role `ADMIN`. Mutation yêu cầu CSRF token và trả RFC 7807 khi lỗi.

| Method | Route | Mục đích |
|---|---|---|
| GET | `/api/v1/admin/shipping-methods` | Danh sách, default trước rồi display order |
| GET | `/api/v1/admin/shipping-methods/{id}` | Chi tiết cùng phụ phí tỉnh |
| POST | `/api/v1/admin/shipping-methods` | Tạo phương thức ở trạng thái `INACTIVE` |
| PATCH | `/api/v1/admin/shipping-methods/{id}` | Sửa cấu hình theo `expectedRevision` |
| POST | `/api/v1/admin/shipping-methods/{id}/activate` | Kích hoạt |
| POST | `/api/v1/admin/shipping-methods/{id}/deactivate` | Ngừng hoạt động; không được tắt default |
| POST | `/api/v1/admin/shipping-methods/{id}/make-default` | Chuyển default nguyên tử |
| PUT | `/api/v1/admin/shipping-methods/{id}/province-rates/{province}` | Upsert phụ phí và tăng revision cha |
| DELETE | `/api/v1/admin/shipping-methods/{id}/province-rates/{province}?expectedRevision=N` | Xóa phụ phí và tăng revision cha |

### Tạo phương thức

```json
{
  "code": "STANDARD",
  "name": "Giao hàng tiêu chuẩn",
  "description": "Giao trong giờ hành chính",
  "baseFee": 25000,
  "freeShippingThreshold": 500000,
  "estimatedMinDays": 2,
  "estimatedMaxDays": 5,
  "displayOrder": 10
}
```

`code` được chuẩn hóa uppercase và bất biến sau khi tạo.

### Cập nhật cấu hình

```json
{
  "name": "Giao hàng tiêu chuẩn",
  "description": null,
  "baseFee": 30000,
  "freeShippingThreshold": 600000,
  "estimatedMinDays": 2,
  "estimatedMaxDays": 4,
  "displayOrder": 10,
  "expectedRevision": 3
}
```

Các action activate/deactivate/make-default nhận `{ "expectedRevision": 3 }`.

### Upsert phụ phí

```json
{
  "surchargeFee": 10000,
  "isActive": true,
  "expectedRevision": 4
}
```

Mọi mutation khóa method cha trước rate con, tăng `revision` và ghi Audit Log trong cùng transaction.

## Mã lỗi

| HTTP | Code | Ý nghĩa |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Tiền, ETA, tên hoặc input không hợp lệ |
| 401/403 | chuẩn auth | Chưa đăng nhập/không phải Admin |
| 404 | `SHIPPING_METHOD_NOT_FOUND` | Không tồn tại method |
| 404 | `SHIPPING_PROVINCE_RATE_NOT_FOUND` | Không tồn tại override tỉnh |
| 409 | `SHIPPING_METHOD_CODE_CONFLICT` | Trùng code |
| 409 | `SHIPPING_METHOD_STALE` | Revision client đã cũ |
| 409 | `SHIPPING_METHOD_NOT_ACTIVE` | Method chưa active để làm default |
| 409 | `SHIPPING_DEFAULT_REQUIRED` | Đang cố tắt method mặc định |

## Audit actions

`SHIPPING_METHOD_CREATED`, `SHIPPING_METHOD_UPDATED`, `SHIPPING_METHOD_ACTIVATED`,
`SHIPPING_METHOD_DEACTIVATED`, `SHIPPING_METHOD_DEFAULT_CHANGED`,
`SHIPPING_PROVINCE_RATE_UPSERTED`, `SHIPPING_PROVINCE_RATE_REMOVED`.

## Customer quote API

`POST /api/v1/shipping/quotes`

- Yêu cầu session role `CUSTOMER`, CSRF token và chịu rate limit 30 request/phút/IP ở cấu hình mặc định.
- Địa chỉ phải thuộc chính Customer; địa chỉ không tồn tại và khác owner đều trả cùng `404 ADDRESS_NOT_FOUND`.
- Client chỉ gửi SKU, số lượng và Flash Sale offer ID; không được gửi subtotal.
- Server tự kiểm tra sản phẩm/SKU, tồn khả dụng, campaign, quota và per-customer limit.
- Quote không mutate tồn kho, quota hoặc giỏ hàng và trả `Cache-Control: no-store`.

```json
{
  "shippingAddressId": "00000000-0000-0000-0000-000000000001",
  "lines": [
    {
      "productSkuId": "00000000-0000-0000-0000-000000000002",
      "quantity": 1,
      "flashSaleOfferId": null
    }
  ]
}
```

Response gồm `shippingAddressId`, `destinationProvinceKey`, `merchandiseSubtotal` và danh sách `quotes`. Mỗi quote chứa method ID/code/name, phí cơ bản, phụ phí, phí cuối cùng, cờ miễn phí, ETA, revision và cờ default.

Các lỗi nghiệp vụ có thể gồm `SKU_NOT_AVAILABLE`, `OUT_OF_STOCK`, `DUPLICATE_SKU`,
`FLASH_SALE_NOT_FOUND`, `FLASH_SALE_INVALID_OFFER`, `FLASH_SALE_PRICE_INVALID`,
`FLASH_SALE_CAMPAIGN_NOT_ACTIVE`, `FLASH_SALE_QUOTA_EXCEEDED`,
`FLASH_SALE_CUSTOMER_LIMIT_EXCEEDED` và `SHIPPING_METHOD_UNAVAILABLE`.

## Kiểm chứng T48

- Backend integration: **357/357 pass** trên PostgreSQL thật, gồm migration/reapply, calculator, RBAC, revision/default concurrency, quote, checkout zero-leak, snapshot, return và reporting.
- Frontend: **130/130 pass**, oxlint không có lỗi/cảnh báo và production build thành công.
- Playwright Flow 14 chạy trên Docker demo thật: tạo Standard/Express, phụ phí tỉnh, chọn quote, checkout COD, giữ snapshot sau đổi giá, từ chối stale quote rồi fulfillment/thu đúng tổng gồm phí vận chuyển.
- Regression Docker: Flow 3, 6, 7, 11, 12 và 13 pass.
