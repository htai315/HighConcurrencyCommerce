# ADR 0011: Product Lifecycle Management

- Trạng thái: Accepted
- Ngày: 06/11/2026
- Task: T46

## Bối cảnh

Product hiện chỉ có `DRAFT`, `PUBLISHED`, `ARCHIVED` và không có API vận hành để tạm ngừng bán hoặc lưu trữ. Chỉ kiểm tra trạng thái Product sau khi khóa SKU trong Checkout tạo ra khoảng đua: Admin có thể suspend trong khi Checkout vẫn dựa trên snapshot cũ.

## Quyết định

Thêm `SUSPENDED` và áp state machine:

```text
DRAFT -> PUBLISHED -> SUSPENDED -> PUBLISHED
                              \-> ARCHIVED
```

`ARCHIVED` là terminal và chỉ đi từ `SUSPENDED`. Lifecycle mutation khóa `Product FOR UPDATE`. Checkout mở rộng lock order thành `Customer -> Voucher -> Product -> Flash Sale -> SKU -> Inventory`, trong đó mọi tập ID được khóa theo UUID tăng dần. Cart/Wishlist mutation khóa Customer trước Product.

Suspend không xóa hoặc viết ngược SKU, tồn kho, Cart, Wishlist, Review, Order, Voucher hay Flash Sale quota. Public projection và Checkout chỉ chấp nhận `PUBLISHED`. Offer Flash Sale của Product suspended tạm mất hiệu lực và có thể hiệu lực lại khi resume nếu Campaign vẫn còn hạn.

T47 cho phép revision trên `PUBLISHED` và `SUSPENDED`; approval không thay đổi lifecycle. Archive bị chặn khi còn revision mở hoặc offer thuộc Campaign đang/sắp hiệu lực.

## Hệ quả

- Suspend và Checkout có thứ tự tuyến tính rõ ràng; không có đơn “lọt qua” sau khi suspend đã giữ lock trước.
- Cart/Wishlist lịch sử vẫn có placeholder để Customer xóa thủ công.
- Resume có thể làm Flash Sale offer hiện lại; UI Admin phải cảnh báo.
- Product archived không khôi phục trong V1; mọi lịch sử vẫn được giữ.
- Product lock làm tăng contention trong Checkout nhưng phạm vi hàng nhỏ và thứ tự UUID giảm nguy cơ deadlock.
