# T43 — Quản trị danh mục sản phẩm

**Ngày:** 06/11/2026

## Mục tiêu

Cho phép Admin quản trị taxonomy dùng bởi Storefront và luồng tạo sản phẩm, thay cho việc chỉ có danh mục seed cố định.

## Quy tắc nghiệp vụ

- Chỉ `ADMIN` được xem danh sách nội bộ và mutation danh mục.
- `name` dài 2–180 ký tự; `slug` dài 2–220 ký tự, chữ thường ASCII, số và dấu gạch ngang.
- `slug` duy nhất không phân biệt thao tác đồng thời; unique violation phải trả `409 CATEGORY_SLUG_CONFLICT`, không trả 500.
- Có thể ẩn/hiện danh mục. Danh mục ẩn biến mất khỏi bộ lọc/navigation công khai nhưng sản phẩm vẫn bán và hiển thị với category `null`.
- Chỉ được xóa danh mục không có sản phẩm tham chiếu. Race giữa gắn sản phẩm và xóa phải trả `409 CATEGORY_IN_USE`.
- Create/update/delete phải ghi Audit Log trong cùng transaction.

## Phạm vi triển khai

1. Application contracts và service PostgreSQL có row lock khi update/delete.
2. API `/api/v1/admin/categories` với RFC 7807, RBAC và CSRF.
3. Workspace Admin React: danh sách, tìm kiếm, tạo, sửa, ẩn/hiện và xóa có xác nhận.
4. Integration tests RBAC, lifecycle, audit, public visibility, conflict và concurrency.
5. Frontend component tests, tài liệu API, release gate và Graphify.

## Definition of Done

- Không có duplicate slug hoặc lỗi 500 dưới concurrent create/update.
- Không xóa được danh mục đang được tham chiếu.
- Public catalog phản ánh visibility đúng contract.
- Backend/frontend tests, lint và production build đều xanh.
