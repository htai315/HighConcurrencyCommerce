# ADR 0003 — Hoãn PayOS, hoàn thiện COD end-to-end trước

Trạng thái: **Đã được chủ dự án chốt** ngày 30/10/2026. Phạm vi roadmap T19–T26.

## Bối cảnh

Checkout core đã có nhánh PayOS về mặt dữ liệu, nhưng chưa kiểm chứng sandbox chính thức về `expiredAt`, canonical signature, webhook ACK/retry, tra cứu transaction và tính idempotent của provider order code. Tiếp tục code bằng giả định có thể làm sai luồng tiền thật.

Trong khi đó COD đã có checkout, reservation, giới hạn hai đơn pending, Staff confirm và Worker expiry. Có thể hoàn thiện luồng fulfillment/thu tiền COD mà không phụ thuộc dịch vụ ngoài.

## Quyết định

- T19–T22 được **deferred**, không coi là hủy. Không triển khai PayOS adapter, payment attempts, webhook, reconciliation hoặc refund cho tới khi có sandbox credential và kiểm chứng contract thực tế.
- Tạm chặn tạo checkout mới với `paymentMethod=PAYOS` bằng lỗi ổn định `PAYOS_DEFERRED` (409). UI hiển thị PayOS là “sắp mở”, không cho chọn.
- Không mock/chế signature, webhook payload, provider acknowledgment, link thanh toán hoặc transaction thật trong production code.
- Giữ schema Payment/Attempt/Callback/Reconciliation hiện tại và ADR 0002 làm thiết kế nền; không đổi state model chỉ vì hoãn tích hợp.
- Roadmap chuyển sang T25 rồi T26 để hoàn thiện COD: fulfillment, xuất kho, giao thành công/thu tiền, giao thất bại/retry/return stock.
- T25 chỉ vận hành Order COD đã `CONFIRMED`. PayOS Order không được đi vào fulfillment cho tới khi Payment `SUCCESS` được triển khai và xác minh.

## Invariant COD fulfillment

- Order `CONFIRMED → FULFILLING` khi Staff tiếp nhận; tạo duy nhất một Shipment `PENDING_PACKING`.
- `PENDING_PACKING → PACKED` không thay đổi tồn.
- `PACKED → SHIPPING` là thời điểm hàng rời kho: Reservation/Lines `ALLOCATED → CONSUMED`, `PhysicalQty` và `AllocatedQty` cùng giảm, movement `STOCK_OUT` ghi đúng một lần.
- `SHIPPING → DELIVERED` với COD chỉ thành công khi ghi nhận số tiền thực thu đúng `expected_amount` và reference thu tiền duy nhất trong cùng transaction.
- Cùng commit giao thành công: DeliveryAttempt `DELIVERED`, Shipment `DELIVERED`, PaymentTransaction `VERIFIED`, Payment `COD_COLLECTED`, Order `COMPLETED`.
- Retry command hoặc race chỉ một transaction thắng; không thu tiền hay xuất kho hai lần.

## Điều kiện mở lại PayOS

Chỉ mở lại T19 khi có sandbox credential và có thể lưu bằng chứng test cho: tạo link/expiry, signature fixtures chính thức, webhook ACK/retry, query-at-deadline, duplicate/late/amount mismatch. Khi đó cập nhật ADR mới hoặc amendment ADR 0003 trước khi bật lựa chọn PayOS cho Customer.
