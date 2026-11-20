# ADR 0008: Kiểm định và cách ly hàng đổi/trả

- Trạng thái: Accepted
- Ngày: 05/11/2026
- Phạm vi: T41

## Bối cảnh

T39 từng cộng hàng trả trực tiếp vào `available_qty` tại thời điểm Staff xác nhận đã nhận. Hàng vật lý chưa được kiểm tra vì thế có thể được checkout lại, tạo rủi ro bán hàng lỗi hoặc thiếu phụ kiện.

## Quyết định

Luồng trả hàng dùng state machine `APPROVED -> RECEIVED -> INSPECTED -> REFUNDED`.

- `RECEIVED`: tăng `physical_qty` và `quarantined_qty`; không tăng `available_qty`.
- `INSPECTED`: Staff phân loại đủ từng dòng thành `RESTOCK`, `QUARANTINE`, `DAMAGED`, `SCRAPPED`.
- `RESTOCK`: chuyển cách ly sang khả dụng.
- `QUARANTINE`: tiếp tục nằm trong cách ly.
- `DAMAGED`: chuyển cách ly sang kho hư hỏng.
- `SCRAPPED`: giảm cách ly và giảm tồn vật lý.
- Admin chỉ được hoàn tiền sau `INSPECTED`.

Bất biến tồn kho mới: `Physical = Available + Reserved + Allocated + Quarantined + Damaged`.

Thứ tự khóa thống nhất: `Order -> Return -> Payment -> SKU (ID tăng dần) -> InventoryBalance (SKU ID tăng dần)`.

## Idempotency và đối soát

Kết quả kiểm định được lưu trên từng dòng. Replay cùng payload trả kết quả hiện có; payload khác trả `409 IDEMPOTENCY_CONFLICT`. Mỗi disposition tạo movement duy nhất theo `(sku, movement_type, RETURN_INSPECTION, return_id)` và mọi cập nhật/audit/notification commit trong một transaction.

## Hệ quả

Hàng chưa kiểm định không thể được checkout. Báo cáo tồn kho và UI hiển thị thêm `quarantined` và `damaged`. Dữ liệu T39 cũ vẫn đọc được; mọi refund mới phải qua `INSPECTED` ở application layer.
