# ADR 0009: Đổi SKU trực tiếp và shipment thay thế

- Trạng thái: Accepted
- Ngày: 05/11/2026
- Task: T42

## Quyết định

Customer có thể chọn `EXCHANGE` thay vì `RETURN` cho một hoặc nhiều dòng của đơn COD đã giao. SKU đích phải khác SKU nguồn, cùng Product, ACTIVE và Product PUBLISHED. V1 không thu hoặc hoàn chênh lệch; Payment, Voucher và Flash Sale của đơn gốc không bị viết ngược.

Admin approval giữ tồn SKU đích atomically (`Available -> Reserved`). Sau khi hàng nguồn được nhận và kiểm định, allocation chuyển `Reserved -> Allocated`; fulfillment thay thế xuất `Allocated` và `Physical`. Allocation hết hạn được giải phóng đúng một lần.

Không tái sử dụng `stock_reservations` và `shipments`: chúng thuộc SalesOrder, có FK/unique one-to-one với `order_id`. T42 dùng `order_exchange_lines`, `exchange_stock_allocations`, `exchange_shipments` và `exchange_delivery_attempts` riêng.

Lock order: `SalesOrder -> OrderReturnRequest -> Payment (khi cần) -> ExchangeAllocation -> ProductSku (ID tăng) -> InventoryBalance (SKU ID tăng) -> ExchangeShipment -> ExchangeDeliveryAttempt`.

Hàng nguồn và replacement giao thất bại đều vào quarantine trước khi có thể trở lại available.
