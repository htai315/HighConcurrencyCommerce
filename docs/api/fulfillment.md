# COD Fulfillment API — Tasks T25–T26

Quyết định nền: [ADR 0003](../adr/0003-defer-payos-complete-cod-first.md). T25 chỉ xử lý đơn `COD` đã được Staff xác nhận; PayOS không đi vào hàng đợi này.

## State flow và invariant

```text
Order CONFIRMED
  → start: Order FULFILLING + Shipment PENDING_PACKING
  → pack: Shipment PACKED
  → handover: Shipment SHIPPING + Reservation/Lines CONSUMED + STOCK_OUT
  → delivered: Shipment DELIVERED + Payment COD_COLLECTED + Order COMPLETED
  → failed: Shipment DELIVERY_FAILED
      → retry: Shipment SHIPPING (tối đa hai lần, attempts 2–3)
      → return-stock: RETURN_IN + Payment VOID + Order CANCELLED
```

- Tất cả endpoint yêu cầu session role `STAFF`; Guest nhận 401, Customer/Admin nhận 403. Mutation yêu cầu CSRF và response không được cache.
- Mỗi Order chỉ có một Shipment. Mỗi bước chỉ nhận đúng trạng thái nguồn; retry/race sau khi bước đã commit trả `409 INVALID_STATE`, không lặp side effect.
- Thứ tự khóa luôn là Order → Shipment → SKU theo UUID → InventoryBalance theo UUID để các flow cạnh tranh dùng cùng lock order.
- Đóng gói không thay đổi tồn. Bàn giao mới là thời điểm hàng rời kho: `PhysicalQty -= quantity`, `AllocatedQty -= quantity`, revision tăng; Reservation/Lines thành `CONSUMED`; mỗi SKU có một movement `STOCK_OUT` âm.
- Giao COD chỉ hoàn tất nếu số tiền thực thu bằng chính xác `Order.TotalAmount` và mã biên nhận chưa được dùng. DeliveryAttempt, PaymentTransaction, Shipment, Payment và Order đổi trạng thái trong một transaction.
- Mỗi lần giao có một DeliveryAttempt. Lần đầu là attempt 1; `redeliveryCount` đếm số lần giao lại và không vượt 2, tương ứng tối đa attempt 3.
- `DELIVERY_FAILED` chỉ được chuyển sang một trong hai nhánh: retry hoặc hoàn kho. Hai command tranh chấp cùng Order/Shipment lock nên không thể cùng commit.
- Hoàn kho chỉ sau khi Staff cung cấp bằng chứng thực nhận. Hệ thống tăng `PhysicalQty/AvailableQty`, ghi `RETURN_IN` đúng một lần và không release Reservation đã `CONSUMED`.

## DTO hàng đợi

`FulfillmentOrderDto` gồm `orderId`, `orderNumber`, `orderStatus`, `shipmentId`, `shipmentStatus`, `paymentStatus`, `customerName`, `totalAmount`, shipping snapshot, timestamps, `redeliveryCount`, `returnStockCompleted` và item snapshots. API không đọc lại địa chỉ/sản phẩm hiện tại để thay nội dung lịch sử đơn.

## Endpoints

### `GET /api/v1/staff/shipments`

Trả các đơn COD ở `CONFIRMED` hoặc `FULFILLING`, theo thời gian tạo tăng dần. Đơn chưa bắt đầu có `shipmentId/shipmentStatus = null`.

### `POST /api/v1/staff/orders/{orderId}/start-fulfillment`

Nhận Order COD `CONFIRMED`, tạo Shipment `PENDING_PACKING` và chuyển Order sang `FULFILLING` trong cùng transaction. Audit: `FULFILLMENT_STARTED`.

### `POST /api/v1/staff/shipments/{shipmentId}/pack`

Chuyển `PENDING_PACKING → PACKED`. Không thay đổi tồn kho. Audit: `SHIPMENT_PACKED`.

### `POST /api/v1/staff/shipments/{shipmentId}/handover`

Chuyển `PACKED → SHIPPING`, tiêu thụ phần tồn đã phân bổ và tạo DeliveryAttempt đầu tiên `IN_PROGRESS`. Audit: `SHIPMENT_HANDED_OVER`.

### `POST /api/v1/staff/shipments/{shipmentId}/delivery-result`

T25 chỉ nhận kết quả thành công:

```json
{
  "result": "DELIVERED",
  "codCollectedAmount": 1250000,
  "collectionReference": "COD-20261030-001"
}
```

`collectionReference` dài 3–100 ký tự, chỉ gồm chữ, số và `._:-`. Thành công tạo PaymentTransaction `provider=COD`, `VERIFIED`, đánh dấu attempt/Shipment `DELIVERED`, Payment `COD_COLLECTED`, Order `COMPLETED`; audit `COD_DELIVERY_COMPLETED`.

Kết quả thất bại không được gửi số tiền/mã biên nhận và bắt buộc có lý do:

```json
{
  "result": "FAILED",
  "failureReason": "Không liên hệ được khách"
}
```

Attempt hiện tại chuyển `FAILED`, Shipment chuyển `DELIVERY_FAILED`; Payment vẫn `COD_PENDING`, Order vẫn `FULFILLING`, không thay đổi tồn. Audit: `DELIVERY_ATTEMPT_FAILED`.

### `POST /api/v1/staff/shipments/{shipmentId}/retry`

Không có body. Chỉ nhận Shipment `DELIVERY_FAILED`, chưa hoàn kho và `redeliveryCount < 2`. Tăng counter, tạo attempt kế tiếp `IN_PROGRESS`, chuyển Shipment về `SHIPPING`. Audit: `SHIPMENT_REDELIVERY_STARTED`.

### `POST /api/v1/staff/shipments/{shipmentId}/return-stock`

```json
{
  "receivedEvidence": "Biên bản nhận lại BB-001"
}
```

Chỉ nhận Shipment `DELIVERY_FAILED` với Reservation/Lines `CONSUMED`, Payment `COD_PENDING`, `returnStockCompleted=false`. Trong một transaction và theo thứ tự lock Order → Shipment → SKU → Balance:

- `PhysicalQty += quantity`, `AvailableQty += quantity`, revision tăng.
- Mỗi SKU ghi một movement `RETURN_IN` dương với source Order.
- `returnStockCompleted=true`; Reservation vẫn `CONSUMED`.
- Payment `COD_PENDING → VOID`.
- Order `FULFILLING → CANCELLED`, `closedReason=DELIVERY_FAILED_RETURNED`.
- Audit `DELIVERY_RETURN_STOCK_COMPLETED`.

## Error codes

- `400 VALIDATION_ERROR`: request giao hàng sai định dạng.
- `400 INVALID_ARGUMENT`: thiếu lý do thất bại/chứng từ hoặc khai báo khoản thu ở kết quả thất bại.
- `404 NOT_FOUND`: không tìm thấy Order/Shipment COD trong phạm vi hợp lệ.
- `409 INVALID_STATE`: sai loại thanh toán hoặc trạng thái đã bị request khác thay đổi.
- `409 INVENTORY_INVARIANT`: phần allocated/physical không đủ lúc xuất kho; transaction rollback.
- `409 COD_AMOUNT_MISMATCH`: số tiền khai báo không khớp tổng đơn; không ghi nhận thu tiền.
- `409 COD_COLLECTION_REFERENCE_REUSED`: mã biên nhận đã thuộc transaction khác.
- `409 REDELIVERY_LIMIT_REACHED`: đã dùng đủ hai lần giao lại.
- `409 RETURN_STOCK_ALREADY_COMPLETED`: hàng đã được hoàn kho bởi request khác.
