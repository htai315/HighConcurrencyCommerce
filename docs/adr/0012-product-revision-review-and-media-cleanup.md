# ADR 0012 — Product revision review và durable media cleanup

Ngày: 06/11/2026  
Trạng thái: Accepted

## Bối cảnh

T47 đã bảo đảm snapshot được áp nguyên tử nhưng Admin chưa có diff live/proposed đủ chi tiết. Ảnh mới của revision bị xóa, cancel hoặc reject trước đây được xóa khỏi storage theo kiểu best-effort sau commit; lỗi I/O làm mất dấu object cần dọn.

## Quyết định

1. `GET /api/v1/admin/product-revisions/{id}/review` là projection read-only, trả diff nội dung, ảnh và thông số cùng `isStale`/`canApprove` từ backend.
2. Approval giữ lock order `Product → Revision`; bốn checkpoint test-only chứng minh mọi thay đổi rollback nếu lỗi trước commit.
3. Mutation terminal và xóa ảnh mới ghi `product_media_cleanup_jobs` trong cùng transaction DB. Worker claim bằng lease ngắn `FOR UPDATE SKIP LOCKED`, xóa storage ngoài transaction và persist từng outcome.
4. Delivery cleanup là at-least-once. `DeleteAsync` phải idempotent khi object đã không tồn tại. Retry dùng exponential backoff có trần; DB chỉ lưu error type đã sanitize.
5. Approval biến ảnh mới thành live (`is_new_upload=false`) và không enqueue cleanup. Approval trên Product `SUSPENDED` không tự resume.

## Hệ quả

- Admin có source-of-truth thống nhất và không tự suy luận eligibility ở client.
- Không còn orphan media bị quên sau lỗi storage tạm thời.
- Revision terminal vẫn giữ metadata; review hiển thị placeholder nếu binary đã cleanup.
- Queue cần monitoring vận hành qua log và truy vấn DB; V1 chưa mở endpoint quản trị worker.
