# ADR 0014: Carrier consignment và ranh giới tracking/fulfillment

- Trạng thái: Accepted
- Ngày: 07/11/2026
- Phạm vi: T49 — Carrier Consignments & Internal Shipment Tracking

## Bối cảnh

T48 đã bổ sung phương thức, phí và ETA vào checkout nhưng `store.shipments` và `store.exchange_shipments` hiện chỉ mô tả state machine fulfillment nội bộ. Hệ thống chưa có carrier, mã vận đơn, nhãn hoặc timeline tracking. Đồng thời, fulfillment COD đang bảo vệ các invariant tiền/tồn quan trọng: chỉ command giao thành công có số tiền chính xác và mã biên nhận duy nhất mới được chuyển Payment, Order, Voucher và DeliveryAttempt trong một transaction.

Nếu đồng nhất trực tiếp trạng thái carrier với `ShipmentStatus`, một event thủ công hoặc webhook giả có thể bypass thu COD, tạo trạng thái đến sai thứ tự hoặc làm hai luồng cập nhật cạnh tranh. Nếu thêm tracking riêng vào cả shipment bán hàng và shipment đổi hàng, code/event contract sẽ bị nhân đôi.

## Quyết định

### 1. Consignment là aggregate vận hành riêng

Tạo `carrier_consignments` liên kết tới đúng một trong hai loại shipment bằng XOR constraint:

- `sales_shipment_id`; hoặc
- `exchange_shipment_id`.

Mỗi shipment có tối đa một consignment trong V1. Consignment lưu carrier, tracking number, snapshot người nhận/địa chỉ/phương thức giao, nhãn và revision. Shipment lịch sử không bắt buộc có consignment.

### 2. Tracking không phải nguồn chân lý commerce

`ConsignmentStatus` mô tả kiện hàng tại carrier; `ShipmentStatus`, `DeliveryAttempt`, `OrderStatus`, `PaymentStatus` và inventory vẫn do fulfillment hiện hữu kiểm soát.

Event carrier `DELIVERED` không được tự động thu COD hoặc hoàn tất Order. Với COD, command fulfillment bắt buộc xác minh số tiền và collection reference; sau khi command này thành công hệ thống mới append tracking event delivered. Tương tự, `RETURNED` không tự hoàn kho nếu chưa qua command return-stock có evidence.

### 3. Timeline append-only và idempotent

`carrier_tracking_events` không update/delete qua application. Mỗi event có `event_key`, canonical `payload_hash`, thời điểm xảy ra và thời điểm ghi nhận.

- Cùng key và cùng hash: replay trả kết quả cũ.
- Cùng key nhưng khác hash: conflict.
- Unique key và payload hash là lớp bảo vệ cuối ở database.

T49 V1 chỉ cho nguồn `STAFF` và `SYSTEM`. Nguồn `PROVIDER`, webhook public và signature verification chỉ được thêm bằng migration/ADR tiếp theo khi có sandbox contract thật.

### 4. Nhãn là immutable file asset

Nhãn PDF/PNG/JPEG được xác minh magic bytes, giới hạn kích thước, tính SHA-256 và lưu ngoài web root qua storage abstraction. Database chỉ giữ immutable file key và metadata. Thay nhãn chỉ hợp lệ trước handover; file cũ được cleanup sau khi transaction commit.

### 5. Thứ tự khóa

Shipment bán hàng:

```text
SalesOrder -> Shipment -> CarrierConsignment -> CarrierTrackingEvent
```

Shipment đổi hàng:

```text
SalesOrder -> OrderReturnRequest -> ExchangeAllocation
-> ExchangeShipment -> CarrierConsignment -> CarrierTrackingEvent
```

Không service nào được khóa consignment trước rồi quay lại Order/Shipment. Handover qua UI mới phải gọi chung fulfillment orchestrator để stock-out, DeliveryAttempt, consignment event, Audit và Notification commit một lần.

### 6. Provider-neutral V1

Carrier master data chỉ là danh mục nội bộ. Không gọi GHN/GHTK/Viettel Post, không mua nhãn, không đối soát cước và không giả lập webhook. `tracking_url_template` chỉ tạo link HTTPS cho Customer nếu Admin cấu hình hợp lệ.

## Hệ quả

- Tracking có thể phát triển độc lập mà không làm yếu invariant COD/tồn kho.
- Sales shipment và exchange shipment dùng chung contract/timeline.
- Đơn legacy vẫn hoạt động khi không có consignment.
- Trạng thái carrier và trạng thái fulfillment có thể khác nhau trong khoảng ngắn; UI phải giải thích rõ và service tích hợp append event hệ thống tại các command tin cậy.
- Split shipment/multi-package cần thiết kế mới vì V1 cố ý giới hạn một consignment cho mỗi shipment.

## Phương án bị loại

- **Thêm tracking fields trực tiếp vào hai bảng shipment:** nhân đôi schema/service và khó giữ contract đồng nhất.
- **Dùng tracking event để tự chuyển Order/Payment:** tạo đường vòng bypass COD verification.
- **Lưu timeline trong JSON:** không có unique event key, index thời gian hoặc constraint idempotency đáng tin cậy.
- **Mở webhook giả trước khi có provider:** không thể chứng minh signature, retry, ordering và ACK contract.
- **Cho phép nhiều tracking number trên một shipment V1:** biến split shipment thành tính năng ngầm, không có allocation theo package.
