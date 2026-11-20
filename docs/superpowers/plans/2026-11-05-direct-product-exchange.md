# Implementation Plan — T42: Direct Product Exchange & Replacement Shipment

- Ngày lập: 05/11/2026
- Trạng thái: Planned
- Phụ thuộc: T39, T40, T41
- ADR dự kiến: `0009-direct-product-exchange-and-replacement-shipment.md`

## 1. Mục tiêu

Cho phép Customer đổi trực tiếp một SKU đã mua sang SKU khác của cùng sản phẩm mà không phải nhận hoàn tiền rồi đặt đơn mới. Hệ thống phải giữ chắc SKU thay thế, tiếp nhận và kiểm định hàng cũ, giao hàng thay thế đúng một lần, không oversell và không làm sai Payment/Voucher/Flash Sale đã chốt của đơn gốc.

## 2. Quyết định nghiệp vụ đã chốt

### 2.1 Phạm vi V1

- Chỉ áp dụng cho đơn COD đã `COMPLETED`, shipment `DELIVERED`, payment `COD_COLLECTED` và còn trong cửa sổ đổi/trả 7 ngày.
- Customer chọn `resolutionType = RETURN | EXCHANGE` khi tạo yêu cầu.
- `EXCHANGE` chỉ cho đổi sang SKU khác thuộc cùng `Product`, đang `ACTIVE`, Product đang `PUBLISHED`.
- Không thu thêm hoặc hoàn chênh lệch trong T42. Giá trị thanh toán, Voucher và Flash Sale của đơn gốc giữ nguyên; Admin chịu trách nhiệm duyệt lựa chọn SKU thay thế.
- Mỗi dòng hàng trả chỉ có một SKU thay thế và `exchangeQty = requestedQty`.
- Không hỗ trợ đổi sang sản phẩm khác, đổi nhiều lần cho cùng đơn, đổi kèm thanh toán bổ sung hoặc PayOS.

### 2.2 Giữ tồn SKU thay thế

- Khi Admin duyệt yêu cầu EXCHANGE, transaction khóa SKU/balance đích và chuyển `Available -> Reserved`.
- Allocation có hạn 7 ngày kể từ lúc duyệt. Worker giải phóng `Reserved -> Available` nếu hết hạn mà hàng cũ chưa được nhận.
- Nếu hết tồn tại thời điểm duyệt, trả `409 EXCHANGE_TARGET_OUT_OF_STOCK`; không duyệt một phần.
- Replay cùng quyết định không giữ tồn lần hai.

### 2.3 Tiếp nhận và kiểm định

- Hàng cũ vẫn đi qua T41: `Physical + Quarantined` khi nhận, sau đó phân loại RESTOCK/QUARANTINE/DAMAGED/SCRAPPED.
- Với `RETURN`, Payment chuyển `REFUND_REQUIRED` và sau kiểm định chờ Admin hoàn tiền như hiện tại.
- Với `EXCHANGE`, Payment giữ `COD_COLLECTED`; không tạo ManualRefund.
- Sau kiểm định thành công, allocation SKU thay thế chuyển `HELD -> ALLOCATED` và return request chuyển `EXCHANGE_READY`.

### 2.4 Giao hàng thay thế

- Staff đóng gói `EXCHANGE_READY -> EXCHANGE_PACKED`.
- Bàn giao vận chuyển atomically chuyển target inventory `Allocated -> Physical out`, ghi movement `EXCHANGE_STOCK_OUT`, shipment `SHIPPING`.
- Giao thành công chuyển `EXCHANGED` và allocation `CONSUMED`.
- Cho phép tối đa ba lần giao giống fulfillment COD hiện tại.
- Khi giao thất bại cuối cùng và hàng quay về kho, hàng thay thế đi vào `Quarantined`, không cộng thẳng `Available`; request chuyển `EXCHANGE_DELIVERY_FAILED` để Admin quyết định đổi lại hoặc chuyển sang hoàn tiền.

## 3. State machine

```text
REQUESTED
  ├─ REJECTED
  ├─ CANCELLED
  └─ APPROVED + target allocation HELD
       ├─ EXCHANGE_EXPIRED + release target stock
       └─ RECEIVED
            └─ INSPECTED
                 └─ EXCHANGE_READY + allocation ALLOCATED
                      └─ EXCHANGE_PACKED
                           └─ EXCHANGE_SHIPPING
                                ├─ EXCHANGED + allocation CONSUMED
                                └─ EXCHANGE_DELIVERY_FAILED
```

Luồng `RETURN` hiện hữu vẫn là `REQUESTED -> APPROVED -> RECEIVED -> INSPECTED -> REFUNDED`.

## 4. Thiết kế dữ liệu — migration 013

### 4.1 Mở rộng `order_return_requests`

- `resolution_type varchar(16) NOT NULL DEFAULT 'RETURN' CHECK (RETURN, EXCHANGE)`.
- Mở rộng status check cho `EXCHANGE_READY`, `EXCHANGE_PACKED`, `EXCHANGE_SHIPPING`, `EXCHANGED`, `EXCHANGE_EXPIRED`, `EXCHANGE_DELIVERY_FAILED`.
- Lifecycle constraint mới phải tương thích dữ liệu T39–T41 cũ.

### 4.2 `order_exchange_lines`

- `id`, `return_request_line_id UNIQUE`, `source_sku_id`, `target_sku_id`, `quantity`.
- Snapshot: target SKU code, option label, regular price tại thời điểm yêu cầu.
- CHECK target khác source, quantity > 0.
- Application guard target cùng Product; FK không thể tự bảo đảm rule liên bảng này.

### 4.3 `exchange_stock_allocations`

- Một allocation cho mỗi exchange line.
- Status: `HELD | ALLOCATED | RELEASED | CONSUMED`.
- `reserved_at`, `expires_at`, `released_at`, `release_reason`, `revision`.
- Unique `(return_request_id, target_sku_id)` để chống giữ trùng.

Không tái sử dụng `stock_reservations`: bảng hiện tại bắt buộc `order_id UNIQUE` và line bắt buộc `order_item_id UNIQUE`, thuộc lifecycle checkout của đơn gốc.

### 4.4 `exchange_shipments` và `exchange_delivery_attempts`

- Shipment thuộc `return_request_id UNIQUE`, không thuộc `sales_order`.
- Status: `PENDING_PACKING | PACKED | SHIPPING | DELIVERED | DELIVERY_FAILED`.
- Có `redelivery_count`, `return_stock_completed`, timestamps và evidence.
- Attempt 1–3, unique `(exchange_shipment_id, attempt_no)`.

Không mở rộng `shipments` hiện tại vì `shipments.order_id` đang NOT NULL + UNIQUE và là navigation one-to-one của SalesOrder.

### 4.5 Inventory movements mới

- `EXCHANGE_RESERVE`: `Available -> Reserved`.
- `EXCHANGE_RELEASE`: `Reserved -> Available`.
- `EXCHANGE_ALLOCATE`: `Reserved -> Allocated`.
- `EXCHANGE_STOCK_OUT`: giảm `Allocated` và `Physical`.
- `EXCHANGE_RETURN_IN`: tăng `Physical` và `Quarantined` khi giao thay thế thất bại và hàng quay về.

Unique movement identity dùng `(sku, movement_type, source_type='ORDER_EXCHANGE', source_id)`.

## 5. Transaction và lock order

Lock order bắt buộc trên mọi command T42:

1. `UserAccount` khi command phụ thuộc Customer limit/ownership.
2. `SalesOrder` gốc.
3. `OrderReturnRequest`.
4. `Payment` nếu command đọc/chuyển trạng thái tiền.
5. `ExchangeStockAllocation` theo ID tăng dần.
6. `ProductSku` gồm source và target, sắp xếp theo ID tăng dần.
7. `InventoryBalance` theo `product_sku_id` tăng dần.
8. `ExchangeShipment`.
9. `ExchangeDeliveryAttempt`.

Không gọi email/provider/network trong transaction. Notification in-app và Audit Log commit cùng transaction nghiệp vụ.

## 6. Backend/API

### Customer

- Mở rộng `GET /orders/{orderId}/return-eligibility` trả danh sách target SKU hợp lệ.
- Mở rộng `POST /orders/{orderId}/returns` với `resolutionType` và `targetSkuId` trên từng line EXCHANGE.
- Trang lịch sử/chi tiết trả về exchange allocation và replacement shipment status.
- Customer chỉ được hủy khi `REQUESTED`; sau approval target stock đã được giữ nên không tự hủy.

### Admin

- Approve EXCHANGE thực hiện target reservation nguyên tử.
- Reject không tác động kho khi request chưa approve.
- Action xử lý `EXCHANGE_DELIVERY_FAILED`: tạo đợt thay thế mới nếu còn tồn hoặc chuyển sang refund workflow có audit rõ ràng.
- Admin không được gọi manual refund trực tiếp cho request EXCHANGE đang hoạt động.

### Staff

- Queue nhận/kiểm định tái sử dụng panel T41, hiển thị rõ RETURN/EXCHANGE và target SKU.
- API replacement fulfillment:
  - `POST /staff/exchanges/{returnId}/pack`
  - `POST /staff/exchanges/{returnId}/handover`
  - `POST /staff/exchange-shipments/{shipmentId}/delivery-result`
  - `POST /staff/exchange-shipments/{shipmentId}/retry`
  - `POST /staff/exchange-shipments/{shipmentId}/return-stock`

### Worker

- `ExchangeAllocationExpiryWorker` claim batch bằng `FOR UPDATE SKIP LOCKED`.
- Release đúng một lần allocation HELD quá hạn và ghi Audit/Notification.
- Có CLI/service method deterministic để integration test không phụ thuộc polling timing.

## 7. Frontend

### Customer

- `CreateReturnModal`: chọn “Hoàn tiền” hoặc “Đổi sản phẩm”.
- Khi đổi: hiển thị target variants cùng Product, tình trạng còn hàng và cảnh báo không xử lý chênh lệch giá trong V1.
- `/account/returns`: timeline riêng cho hàng đổi và trạng thái giao thay thế.

### Admin

- `AdminReturnsPanel`: badge resolution type; xem source/target; approve kèm xác nhận giữ tồn.
- Queue riêng cho exchange delivery failed và action chuyển sang refund.

### Staff

- `StaffReturnsPanel`: sau inspection chuyển request sang khu vực “Đơn đổi sẵn sàng giao”.
- Panel fulfillment thay thế có pack/handover/delivery/retry/return-stock, selector ổn định bằng `data-testid`.

## 8. Error contract

- `EXCHANGE_TARGET_REQUIRED` — 400.
- `EXCHANGE_TARGET_INVALID` — 400, khác Product/source SKU hoặc SKU không active/published.
- `EXCHANGE_TARGET_OUT_OF_STOCK` — 409.
- `EXCHANGE_ALLOCATION_EXPIRED` — 409.
- `EXCHANGE_ALREADY_FULFILLED` — 409.
- `EXCHANGE_INVALID_STATE` — 400.
- `IDEMPOTENCY_CONFLICT` — 409.
- Owner isolation tiếp tục trả 404.

## 9. Kiểm thử bắt buộc

### Migration/persistence

- Migration 013 chạy mới và replay an toàn.
- Dữ liệu RETURN cũ backfill `resolution_type='RETURN'` và vẫn hợp lệ.
- Constraints/status/FK/unique index hoạt động trên PostgreSQL thật.

### Integration

- RBAC và owner isolation Customer/Staff/Admin.
- Target SKU phải cùng Product, active/published và khác source.
- Hai Admin approve đồng thời slot tồn cuối: đúng một request giữ được tồn, request còn lại 409.
- Approve/cancel/expiry tranh chấp không leak Reserved.
- Receive/inspect EXCHANGE không chuyển Payment sang `REFUND_REQUIRED`.
- Inspect chuyển HELD -> ALLOCATED đúng một lần.
- Handover/stock-out concurrent không âm tồn, không stock-out hai lần.
- Delivery retry tối đa ba lần; permanent failure/return-in đúng một lần vào quarantine.
- Refund endpoint chặn EXCHANGE chưa chuyển workflow.
- Inventory partition luôn đúng sau mọi nhánh.

### Frontend/E2E

- Unit/component tests cho chọn resolution/target SKU, Admin approve, Staff fulfillment và timeline Customer.
- Playwright `08-direct-product-exchange.spec.ts` chạy Docker thật:
  1. Customer mua và nhận đơn COD.
  2. Tạo yêu cầu đổi sang variant khác.
  3. Admin duyệt và giữ target stock.
  4. Staff nhận + kiểm định hàng cũ.
  5. Staff pack + handover + giao replacement.
  6. Customer thấy `EXCHANGED`; inventory/audit/notification đúng.

## 10. Thứ tự triển khai

1. ADR 0009 và migration 013.
2. Models/EF mapping/fixture + migration tests.
3. Contracts và eligibility/create request EXCHANGE.
4. Admin approval + atomic allocation + concurrency tests.
5. Tích hợp receive/inspection T41 và Payment guard.
6. Exchange shipment/fulfillment/retry/return-stock.
7. Expiry worker.
8. Frontend Customer -> Admin -> Staff.
9. Playwright E2E, API docs, release checklist.
10. Full test/build/lint/Docker gate và `graphify update .`.

## 11. Definition of Done

- Không oversell target SKU dưới concurrent approval/checkout.
- Không leak Reserved/Allocated ở reject, expiry hoặc failure.
- Không tạo hai replacement shipment hoặc hai stock-out.
- Payment/Voucher/Flash Sale của đơn gốc không bị viết ngược.
- Hàng cũ và hàng replacement thất bại đều qua quarantine trước khi available.
- Audit/notification có event key idempotent.
- Backend, frontend, lint, build, Compose, migration fresh/legacy và Playwright đều pass.
