# ADR 0016 — Wishlist product alerts bằng PostgreSQL dirty queue và commerce outbox

- Ngày: 07/11/2026
- Trạng thái: **Accepted cho T51**
- Liên quan: T27 Notifications, T33 Email Delivery, T37 Wishlist, T46 Product Lifecycle, T51 Wishlist Product Alerts

## Bối cảnh

Tồn khả dụng của một Product có thể tăng qua nhiều luồng: nhập kho, duyệt điều chỉnh, hủy/expiry reservation, giao thất bại hoàn kho, nhận hàng trả và exchange. Regular price, SKU status và Product lifecycle cũng được thay đổi ở các service khác nhau. Gọi trực tiếp notification/email từ từng service dễ bỏ sót writer mới, tăng transaction coupling và có nguy cơ giữ lock trong lúc SMTP chậm.

Notification T27 hiện ghi đồng transaction và idempotent bằng `(recipient_id, event_key)`. Auth email T33 dùng action-token outbox chuyên biệt; contract đó không phù hợp cho email thương mại.

## Quyết định

1. PostgreSQL trigger chỉ upsert `product_alert_refresh_queue` trong cùng transaction với thay đổi nguồn. Trigger không đọc subscriber, không dựng nội dung và không gọi mạng.
2. Queue coalesce theo Product. `reason_mask` hợp nhất inventory/price/lifecycle; `event_version` tăng trên mỗi wake-up. Evaluator chỉ xóa row nếu version vẫn bằng version đã claim, tránh mất event đến trong lúc xử lý.
3. Evaluator worker claim bằng short lease, đọc trạng thái Product đã commit, rồi khóa Wishlist subscription theo batch. Observed state, sequence, in-app notification và commerce email outbox cùng commit.
4. Business event idempotent bằng event key chứa type/customer/product/sequence. Worker retry không tạo notification/outbox thứ hai.
5. `commerce_email_outbox` độc lập với `auth_email_outbox`. Có thể dùng chung low-level SMTP transport, nhưng không tái sử dụng auth action token/template semantics.
6. SMTP chạy ngoài transaction với per-item timeout. Delivery là at-least-once vì worker có thể crash sau khi SMTP nhận nhưng trước khi persist `sent_at`.
7. Price alert V1 chỉ theo regular price thấp nhất của SKU ACTIVE. Flash Sale/voucher không thuộc signal này.

## Lock order

- Settings API: `UserAccount -> CustomerWishlistItem`.
- Product/inventory writers giữ lock order hiện tại; trigger chỉ upsert queue.
- Evaluator không khóa Product/SKU/Inventory. Nó khóa Wishlist rows theo `(product_id, customer_id)` trong transaction ngắn.
- Email worker claim lease rồi commit trước khi gọi SMTP.

Không được thêm đường khóa ngược từ evaluator sang Customer/Product/SKU/Inventory.

## Hệ quả

Ưu điểm:

- Mọi writer PostgreSQL hiện tại và tương lai đều được bắt tại data boundary.
- Rollback nghiệp vụ tự rollback queue wake-up.
- Không phát email trong transaction và không cần RabbitMQ/Kafka ở quy mô hiện tại.
- Coalesce giảm fan-out thừa khi nhiều SKU thay đổi liên tiếp.

Đánh đổi:

- Trigger là một phần correctness boundary cần migration test.
- Alert là state-transition notification, không phải lịch sử mọi biến động.
- SMTP không thể exactly-once tuyệt đối.
- Product có lượng subscriber lớn có thể cần continuation cursor; chỉ bổ sung sau đo đạc.

## Phương án không chọn

- Gọi notifier từ từng application service: dễ bỏ sót mutation path và tăng coupling.
- In-memory domain event: mất event khi process crash sau commit.
- Dùng auth email outbox: sai domain, payload và lifecycle token.
- Thêm broker ngay: tăng hạ tầng khi chưa có bằng chứng PostgreSQL queue không đạt SLO.

## Điều kiện xem xét lại

Viết ADR mới nếu benchmark production cho thấy queue PostgreSQL không đạt throughput/latency sau batch/index tuning, hoặc nếu cần Web Push/multi-channel orchestration ngoài SMTP và in-app.
