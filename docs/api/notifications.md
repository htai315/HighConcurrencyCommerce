# In-app Notification API — Task T27

## Phạm vi và kiến trúc

Notification là dữ liệu recipient-owned trong PostgreSQL. T27 không dùng RabbitMQ hoặc Outbox: thông báo được `INSERT ... ON CONFLICT DO NOTHING` ngay trong transaction của nghiệp vụ tạo sự kiện. Vì vậy trạng thái đơn và thông báo cùng commit/rollback trong modular monolith.

Khóa idempotency là `(recipient_id, event_key)`, đã có unique constraint trong schema. Event lặp không tạo thông báo thứ hai và không làm thất bại transaction.

Các sự kiện hiện được phát:

- `ORDER_CONFIRMED`: Staff xác nhận COD.
- `ORDER_EXPIRED`: Worker đóng COD hết hạn.
- `ORDER_SHIPPING`: đơn rời kho, attempt đầu.
- `DELIVERY_FAILED`: một attempt giao thất bại; event key có `attemptNo`.
- `ORDER_REDELIVERY`: bắt đầu attempt giao lại.
- `ORDER_DELIVERED`: giao thành công và thu COD.
- `ORDER_CANCELLED`: giao thất bại và hàng đã hoàn kho.
- `WISHLIST_BACK_IN_STOCK`: sản phẩm yêu thích có hàng trở lại.
- `WISHLIST_PRICE_DROP`: regular price thấp nhất của sản phẩm yêu thích giảm.

## Quyền và bảo mật

- Cả Customer, Staff và Admin đã đăng nhập đều dùng được API nhưng chỉ thấy notification có `recipient_id` bằng session hiện tại.
- Guest nhận 401.
- ID của người khác được xử lý như không tồn tại: 404 `NOT_FOUND`, không tiết lộ ownership.
- Mutation yêu cầu CSRF. Response có `Cache-Control: no-store`.

## `GET /api/v1/notifications`

Query:

- `unreadOnly`: boolean, mặc định `false`.
- `limit`: 1–100, mặc định 50.
- `before` và `beforeId`: cursor kép, phải cùng có hoặc cùng vắng.

Response sắp xếp mới nhất trước:

```json
{
  "items": [
    {
      "id": "uuid",
      "type": "ORDER_SHIPPING",
      "title": "Đơn hàng đang được giao",
      "body": "Đơn HC-... đang trên đường giao đến bạn.",
      "actionUrl": null,
      "createdAt": "2026-10-30T08:00:00Z",
      "readAt": null
    }
  ],
  "unreadCount": 1
}
```

`unreadCount` là tổng số chưa đọc của recipient, không chỉ số item trong page hiện tại. `hasMore` cho biết còn page kế tiếp. `actionUrl` chỉ chứa internal path do server tạo; frontend tiếp tục từ chối absolute/protocol-relative URL trước khi render.

## `POST /api/v1/notifications/{id}/read`

Không có body. Chỉ cập nhật notification của session hiện tại. Replay là idempotent: nếu đã đọc, API trả lại cùng `readAt` và không ghi đè thời điểm đọc đầu tiên.

## Error codes

- `400 VALIDATION_ERROR`: query ngoài giới hạn.
- `401 UNAUTHORIZED`: chưa đăng nhập.
- `404 NOT_FOUND`: ID không tồn tại hoặc thuộc recipient khác.

## Giới hạn đã chốt

- Chưa có push realtime hoặc polling tự động.
- Email commerce chỉ áp dụng cho Wishlist Product Alerts qua outbox riêng; notification feed vẫn là nguồn in-app.
- Nếu sau này tách process/broker hoặc phát sự kiện sau commit, phải có ADR riêng cho Transactional Outbox; không tái sử dụng in-memory event như nguồn đảm bảo giao nhận.
