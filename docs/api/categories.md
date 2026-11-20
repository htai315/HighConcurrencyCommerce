# T43 — Category Management API

## Quyền và contract

- Public: `GET /api/v1/catalog/categories` chỉ trả danh mục `isVisible = true`.
- Staff: `GET /api/v1/staff/categories` trả taxonomy nội bộ để gắn danh mục cho Product Draft.
- Admin: toàn bộ route `/api/v1/admin/categories`; mutation bắt buộc CSRF.
- Slug dùng chữ thường ASCII, số và dấu gạch ngang; duy nhất toàn hệ thống.
- Ẩn danh mục không ẩn sản phẩm. Catalog vẫn trả sản phẩm nhưng `category = null`.
- Chỉ xóa được danh mục chưa có sản phẩm tham chiếu.

## Admin endpoints

- `GET /api/v1/admin/categories`
- `POST /api/v1/admin/categories`
- `PATCH /api/v1/admin/categories/{id}`
- `DELETE /api/v1/admin/categories/{id}`

Payload create/update:

```json
{
  "name": "Túi và balo cầu lông",
  "slug": "tui-va-balo-cau-long",
  "isVisible": true
}
```

Response gồm `id`, `name`, `slug`, `isVisible`, `productCount`, `createdAt`, `updatedAt`.

## Error codes

- `400 VALIDATION_ERROR`: name/slug không hợp lệ.
- `404 CATEGORY_NOT_FOUND`: danh mục không tồn tại.
- `409 CATEGORY_SLUG_CONFLICT`: slug bị trùng, kể cả race create/update.
- `409 CATEGORY_IN_USE`: danh mục đang được sản phẩm tham chiếu.

Create/update/delete ghi `CATEGORY_CREATED`, `CATEGORY_UPDATED`, `CATEGORY_DELETED` vào Audit Log trong cùng transaction.
