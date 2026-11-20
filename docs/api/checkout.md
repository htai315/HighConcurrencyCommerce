# Checkout API Contract — Task T17

## `POST /api/v1/checkouts`

Yêu cầu session `CUSTOMER`, CSRF và header `Idempotency-Key` gồm 8–160 ký tự `[A-Za-z0-9._:-]`. Response luôn `Cache-Control: no-store`.

```json
{
  "shippingAddressId": "uuid",
  "shippingMethodId": "uuid",
  "shippingMethodRevision": 3,
  "quotedShippingFee": 30000,
  "paymentMethod": "COD",
  "lines": [
    {
      "productSkuId": "uuid",
      "quantity": 2,
      "flashSaleOfferId": null
    }
  ]
}
```

- `lines`: 1–50 SKU, mỗi SKU duy nhất, quantity 1–99.
- Ba trường shipping lấy nguyên từ `POST /api/v1/shipping/quotes`; checkout mới bắt buộc có method ID, revision dương và phí VND nguyên không âm.
- `flashSaleOfferId`: `null` đối với SKU giá thường; hoặc `UUID` của ưu đãi Flash Sale hợp lệ (thuộc T24).
- Client không gửi giá, tổng tiền, title, trạng thái hoặc tồn kho. Server đọc lại và áp dụng `sale_price` nếu là Flash Sale.
- Địa chỉ bắt buộc thuộc Customer hiện tại.

## Thành công

Lần đầu trả `201 Created`, replay cùng key/payload trả `200 OK` với header `Idempotency-Replayed: true` và cùng response snapshot.

```json
{
  "orderId": "uuid",
  "orderNumber": "HC-20261030-...",
  "orderStatus": "PENDING_COD_CONFIRMATION",
  "paymentMethod": "COD",
  "paymentStatus": "COD_PENDING",
  "totalAmount": 990000,
  "shippingFee": 30000,
  "deadlineAt": "2026-10-31T00:00:00Z",
  "shippingAddress": {
    "recipientName": "...",
    "phone": "...",
    "addressLine": "...",
    "ward": "...",
    "province": "..."
  },
  "items": [],
  "shippingMethod": {
    "shippingMethodId": "uuid",
    "code": "STANDARD",
    "name": "Giao hàng tiêu chuẩn",
    "estimatedMinDays": 2,
    "estimatedMaxDays": 5,
    "revision": 3,
    "destinationProvinceKey": "HO CHI MINH"
  }
}
```

- COD: Order `PENDING_COD_CONFIRMATION`, Payment `COD_PENDING`, deadline 24 giờ.
- PayOS: khi `PayOs:Enabled=true`, tạo Order `PENDING_PAYMENT`, Payment `PENDING`, Reservation `HELD` với deadline 15 phút; payment link được tạo qua endpoint attempt riêng sau khi checkout commit. Khi feature tắt (mặc định), request nhận `409 PAYOS_DEFERRED` và không tạo commerce side effect.

## Transaction và lock order

Transaction PostgreSQL duy nhất:

```text
Customer FOR NO KEY UPDATE
  → ShippingMethod FOR SHARE
  → Voucher FOR UPDATE (nếu có)
  → Product FOR SHARE
  → FlashSaleCampaign FOR UPDATE theo UUID tăng dần (nếu có Flash Sale line)
  → FlashSaleOffer FOR UPDATE theo UUID tăng dần
  → ProductSku FOR UPDATE theo UUID tăng dần
  → InventoryBalance FOR UPDATE theo SKU UUID tăng dần
```

Trong cùng commit:

1. Xác minh địa chỉ owner-only, ShippingMethod active, revision và phí quote còn đúng.
2. Xác minh Product `PUBLISHED`, SKU `ACTIVE`, giá server > 0.
3. Kiểm tra toàn bộ tồn khả dụng trước khi mutation.
4. Tính subtotal sau Flash Sale, voucher discount và shipping fee; `total = subtotal - voucher + shipping`.
5. Tạo Order, OrderItem và shipping snapshot bất biến.
6. Tạo Payment với `ExpectedAmount` đã gồm shipping, StockReservation `HELD` và ReservationLine `HELD`.
7. Với mỗi SKU: `available -= quantity`, `reserved += quantity`, physical/allocated giữ nguyên, revision tăng 1.
8. Ghi movement `RESERVE`, xóa SKU đã checkout khỏi cart và lưu response snapshot.

Các luồng confirm/cancel/expiry/return stock cũng khóa theo chiều Campaign (nếu cần) → Offer → SKU → InventoryBalance. Campaign cancel và checkout tranh chấp cùng campaign lock, nên checkout không thể đọc snapshot `ACTIVE` cũ sau khi cancel đã bắt đầu.

Thiếu một SKU hoặc bất kỳ rule nào sai sẽ rollback mọi Order/Payment/Reservation/movement/inventory/cart. Savepoint chỉ giữ CheckoutRequest và lưu `REJECTED` để replay lỗi ổn định.

## Idempotency

Tuân thủ [ADR 0002](../adr/0002-checkout-idempotency-and-cod-void.md):

- Scope `(customerId, Idempotency-Key)`.
- Checkout mới hash canonical payload `checkout-v3`: voucher chuẩn hóa, address, method ID, method revision, quoted fee, payment method và lines sort theo SKU.
- Bản ghi `checkout-v1`/`checkout-v2` đã `SUCCEEDED` hoặc `REJECTED` trước T48 vẫn replay được bằng payload legacy; payload thiếu shipping với key mới luôn bị từ chối.
- Cùng key/khác payload: `409 IDEMPOTENCY_KEY_REUSED`.
- Business rejection được lưu và replay cùng HTTP/error code.
- Transient DB failure rollback toàn bộ; deadlock/serialization retry tối đa ba lần.
- Lock timeout/in-progress: `409 CHECKOUT_IN_PROGRESS`, `Retry-After: 2`.
- Frontend giữ key trong `sessionStorage` qua lỗi mạng; chỉ xóa khi nhận success hoặc rejection xác định.

## Lỗi ổn định

- `400 INVALID_IDEMPOTENCY_KEY`, `VALIDATION_ERROR`, `DUPLICATE_SKU`, `FLASH_SALE_NOT_SUPPORTED`.
- `401/403` theo auth/RBAC.
- `404 ADDRESS_NOT_FOUND`.
- `409 OUT_OF_STOCK`, `SKU_NOT_AVAILABLE`, `SHIPPING_METHOD_UNAVAILABLE`, `SHIPPING_QUOTE_CHANGED`, `IDEMPOTENCY_KEY_REUSED`, `CHECKOUT_IN_PROGRESS`, `CONCURRENCY_CONFLICT`, `COD_PENDING_LIMIT_EXCEEDED`, `PAYOS_DEFERRED`.

Flash Sale hỗ trợ COD; PayOS chỉ khả dụng khi feature flag đã bật và tuân theo lifecycle tại [PayOS API](payos.md).

Quy trình xác nhận và expiry COD tiếp tục tại [COD API](cod-orders.md).
