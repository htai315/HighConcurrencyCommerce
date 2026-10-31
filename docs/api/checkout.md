# Checkout API Contract — Task T17

## `POST /api/v1/checkouts`

Yêu cầu session `CUSTOMER`, CSRF và header `Idempotency-Key` gồm 8–160 ký tự `[A-Za-z0-9._:-]`. Response luôn `Cache-Control: no-store`.

```json
{
  "shippingAddressId": "uuid",
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
  "deadlineAt": "2026-10-31T00:00:00Z",
  "shippingAddress": {
    "recipientName": "...",
    "phone": "...",
    "addressLine": "...",
    "ward": "...",
    "province": "..."
  },
  "items": []
}
```

- COD: Order `PENDING_COD_CONFIRMATION`, Payment `COD_PENDING`, deadline 24 giờ.
- PayOS: thiết kế T17 từng chuẩn bị Order `PENDING_PAYMENT`, nhưng tạo checkout PayOS hiện bị khóa theo [ADR 0003](../adr/0003-defer-payos-complete-cod-first.md) cho tới khi kiểm chứng sandbox. Request nhận `409 PAYOS_DEFERRED` và không tạo commerce side effect.

## Transaction và lock order

Transaction PostgreSQL duy nhất:

```text
Customer FOR UPDATE
  → FlashSaleCampaign FOR UPDATE theo UUID tăng dần (nếu có Flash Sale line)
  → FlashSaleOffer FOR UPDATE theo UUID tăng dần
  → ProductSku FOR UPDATE theo UUID tăng dần
  → InventoryBalance FOR UPDATE theo SKU UUID tăng dần
```

Trong cùng commit:

1. Xác minh địa chỉ owner-only, Product `PUBLISHED`, SKU `ACTIVE`, giá server > 0.
2. Kiểm tra toàn bộ tồn khả dụng trước khi mutation.
3. Tạo Order và OrderItem snapshot title/SKU option/giá.
4. Tạo Payment, StockReservation `HELD` và ReservationLine `HELD`.
5. Với mỗi SKU: `available -= quantity`, `reserved += quantity`, physical/allocated giữ nguyên, revision tăng 1.
6. Ghi movement `RESERVE`, `qtyDelta = quantity`, source `SALES_ORDER`.
7. Xóa các SKU đã checkout khỏi Customer cart.
8. Lưu CheckoutRequest `SUCCEEDED` và response snapshot.

Các luồng confirm/cancel/expiry/return stock cũng khóa theo chiều Campaign (nếu cần) → Offer → SKU → InventoryBalance. Campaign cancel và checkout tranh chấp cùng campaign lock, nên checkout không thể đọc snapshot `ACTIVE` cũ sau khi cancel đã bắt đầu.

Thiếu một SKU hoặc bất kỳ rule nào sai sẽ rollback mọi Order/Payment/Reservation/movement/inventory/cart. Savepoint chỉ giữ CheckoutRequest và lưu `REJECTED` để replay lỗi ổn định.

## Idempotency

Tuân thủ [ADR 0002](../adr/0002-checkout-idempotency-and-cod-void.md):

- Scope `(customerId, Idempotency-Key)`.
- Hash canonical payload `checkout-v1`, lines sort theo SKU.
- Cùng key/khác payload: `409 IDEMPOTENCY_KEY_REUSED`.
- Business rejection được lưu và replay cùng HTTP/error code.
- Transient DB failure rollback toàn bộ; deadlock/serialization retry tối đa ba lần.
- Lock timeout/in-progress: `409 CHECKOUT_IN_PROGRESS`, `Retry-After: 2`.
- Frontend giữ key trong `sessionStorage` qua lỗi mạng; chỉ xóa khi nhận success hoặc rejection xác định.

## Lỗi ổn định

- `400 INVALID_IDEMPOTENCY_KEY`, `VALIDATION_ERROR`, `DUPLICATE_SKU`, `FLASH_SALE_NOT_SUPPORTED`.
- `401/403` theo auth/RBAC.
- `404 ADDRESS_NOT_FOUND`.
- `409 OUT_OF_STOCK`, `SKU_NOT_AVAILABLE`, `IDEMPOTENCY_KEY_REUSED`, `CHECKOUT_IN_PROGRESS`, `CONCURRENCY_CONFLICT`, `COD_PENDING_LIMIT_EXCEEDED`, `PAYOS_DEFERRED`.

Flash Sale hiện hỗ trợ COD theo [ADR 0004](../adr/0004-enable-cod-for-flash-sale-while-payos-deferred.md). PayOS vẫn deferred theo ADR 0003.

Quy trình xác nhận và expiry COD tiếp tục tại [COD API](cod-orders.md).
