# T18 — COD pending limit, Staff confirmation và expiry Worker

## Mục tiêu

Hoàn thiện vòng đời giữ hàng COD trước fulfillment: không vượt hai đơn pending mỗi Customer, Staff xác nhận chuyển giữ sang phân bổ, quá hạn tự nhả hàng và VOID payment exactly-once.

## Phần đã thực hiện

1. Guard COD pending limit trong transaction checkout đã khóa Customer.
2. Staff-only queue cho đơn COD chờ xác nhận.
3. Confirm service khóa Order → SKU → Balance, cập nhật Order/Reservation/Inventory/movement/audit atomically.
4. Hosted Worker polling configurable, xử lý từng Order quá hạn trong transaction riêng.
5. Expiry release tồn, đóng Order/Payment/Reservation và audit bằng actor WORKER.
6. React Staff Dashboard có tab hàng đợi COD và thao tác xác nhận.
7. Integration tests bao phủ RBAC, concurrent limit, confirm lifecycle, double confirm và nhiều vòng Worker exactly-once.

## Chuyển tiếp

T19 xác minh PayOS sandbox. T21 tiếp tục pressure-test race payment/cancel/expiry. T25 dùng Reservation `ALLOCATED` để xuất kho và thu COD khi delivered.
