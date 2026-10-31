# COD Confirmation & Expiry API — Task T18

## Quy tắc nghiệp vụ

- Mỗi Customer có tối đa **2** Order COD ở trạng thái `PENDING_COD_CONFIRMATION`.
- Guard giới hạn nằm trong cùng customer-lock của checkout. Hai request đồng thời khi đã có một đơn pending chỉ cho đúng một request tạo đơn thứ hai.
- Deadline xác nhận là 24 giờ từ checkout.
- Staff xác nhận trong hạn chuyển Order `PENDING_COD_CONFIRMATION → CONFIRMED`.
- Payment vẫn `COD_PENDING` sau xác nhận; T25 ghi `COD_COLLECTED` chỉ khi giao hàng và thực thu thành công.
- Xác nhận chuyển Reservation/Lines `HELD → ALLOCATED`, đồng thời chuyển tồn `ReservedQty → AllocatedQty`.
- Worker hết hạn chuyển Order sang `EXPIRED`, Payment `COD_PENDING → VOID`, Reservation/Lines `HELD → RELEASED` và tồn `ReservedQty → AvailableQty`.
- Confirm và expiry khóa Order trước, sau đó SKU/Inventory theo UUID tăng dần. Guard trạng thái bảo đảm side effect exactly-once trên nhiều API/Worker instance.
- Mỗi confirm/expiry ghi InventoryMovement và AuditLog trong cùng transaction.

## Staff endpoints

Yêu cầu role `STAFF`; Customer/Admin nhận 403. Mutation yêu cầu CSRF. Response `Cache-Control: no-store`.

### `GET /api/v1/staff/orders/cod-pending`

Trả hàng đợi COD theo deadline tăng dần. DTO gồm thông tin Order, Customer, shipping snapshot và item snapshots cần thiết để Staff xác nhận.

### `POST /api/v1/staff/orders/{id}/confirm-cod`

Không có request body. Thành công trả Order đã `CONFIRMED`.

Transaction:

```text
SalesOrder FOR UPDATE
  → ProductSku FOR UPDATE (sorted)
  → InventoryBalance FOR UPDATE (sorted)
```

Với mỗi line:

```text
ReservedQty -= quantity
AllocatedQty += quantity
Revision += 1
Movement = ALLOCATE, source = SALES_ORDER
```

Reservation và line chuyển `ALLOCATED`; Payment giữ `COD_PENDING`; audit action `COD_ORDER_CONFIRMED`.

## Expiry Worker

`CodExpiryWorker` chạy định kỳ, mặc định 30 giây (`CodExpiry:PollIntervalMilliseconds`). Mỗi vòng lấy tối đa 100 Order quá hạn và xử lý từng Order trong transaction độc lập.

```text
ReservedQty -= quantity
AvailableQty += quantity
Revision += 1
Movement = RELEASE, source = SALES_ORDER
Order = EXPIRED
Payment = VOID
Reservation/Lines = RELEASED
closedReason = COD_CONFIRMATION_EXPIRED
Audit = COD_ORDER_EXPIRED, actorType = WORKER
```

Nhiều Worker có thể cùng thấy một ID, nhưng Order row lock và state guard chỉ cho một transaction áp dụng. Unique movement identity là guard cuối chống ghi lặp.

## Error codes

- Checkout: `409 COD_PENDING_LIMIT_EXCEEDED`.
- Confirm: `404 NOT_FOUND`, `409 INVALID_STATE`, `409 COD_DEADLINE_EXPIRED`, `409 INVENTORY_INVARIANT`.
- Đơn đã được Staff khác xác nhận hoặc Worker expire không được xử lý lần hai.

Luồng sau xác nhận tiếp tục tại [COD Fulfillment API](fulfillment.md).
