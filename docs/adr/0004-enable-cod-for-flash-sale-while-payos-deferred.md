# ADR 0004 — Cho phép Flash Sale qua COD khi PayOS đang hoãn

- Trạng thái: Accepted
- Ngày: 30/10/2026

## Bối cảnh

PayOS đang được hoãn theo ADR 0003 cho tới khi có sandbox credential và bằng chứng kiểm chứng contract. Nếu Flash Sale chỉ chấp nhận PayOS thì toàn bộ luồng T23–T24 không thể được sử dụng trong môi trường hiện tại, dù cơ chế giữ tồn, giữ quota và giới hạn theo khách hàng đã nằm trong cùng transaction checkout.

## Quyết định

Flash Sale được phép checkout bằng COD. Giá sale, quota và tồn kho được giữ nguyên tử trong transaction checkout. Khi Staff xác nhận đơn COD, allocation chuyển `HELD -> SOLD`; khi khách hủy hoặc worker làm hết hạn, allocation chuyển `HELD -> RELEASED` và quota/tồn được trả đúng một lần.

Khi PayOS được kích hoạt sau này, PayOS phải tái sử dụng cùng state machine allocation và các invariant T24; không tạo một cơ chế quota riêng.

## Hệ quả

- Storefront Flash Sale hoạt động được khi PayOS còn deferred.
- Rủi ro giữ quota bằng đơn COD được giới hạn bởi tối đa hai đơn COD chờ xác nhận trên mỗi khách và deadline 24 giờ.
- Việc bật PayOS vẫn cần ADR/integration verification riêng theo ADR 0003.
