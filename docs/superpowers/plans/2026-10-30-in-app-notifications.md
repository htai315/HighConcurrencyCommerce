# T27 — In-app notifications recipient-only

Ngày hoàn tất: 30/10/2026.

## Mục tiêu

Cung cấp feed thông báo trong ứng dụng cho các mốc quan trọng của đơn hàng, bảo vệ tuyệt đối ownership và không tạo bản ghi lặp khi command/event bị retry.

## Kết quả

1. Application contract, PostgreSQL service và authenticated API list/read.
2. Insert idempotent bằng unique `(recipient_id,event_key)` và `ON CONFLICT DO NOTHING`.
3. Notification được tạo trong cùng transaction với COD confirm/expiry và fulfillment transitions.
4. Owner filter ở mọi query; cross-recipient read trả 404.
5. Mark-read idempotent, giữ nguyên `readAt` đầu tiên.
6. React route `/notifications`, navigation cho mọi role đăng nhập, unread count và thao tác đánh dấu đã đọc.

## Bằng chứng

- Backend: 106/106 integration tests pass; test API bao phủ auth, owner isolation, cross-owner 404, read replay và unread filter. Concurrent double-delivery chỉ tạo một `ORDER_DELIVERED` notification.
- Frontend: 44/44 tests pass; test feed/read kiểm tra CSRF và cập nhật unread count.
- .NET build 0 warning/error; React lint/build pass.

## Không thuộc T27

Outbox, broker, push realtime và delivery worker. Các thành phần này chỉ được thêm sau ADR riêng.
