# T45 — Product Media Upload & Local Object Storage

## Mục tiêu

Thay thao tác nhập `fileKey` thủ công bằng upload ảnh thật, lưu bền vững trên volume, phục vụ ảnh công khai qua API và giữ nguyên invariant chỉ Product `DRAFT` mới được thay đổi media.

## Quyết định

- Thêm `IProductMediaStorage`; bản demo dùng filesystem persistent volume. Contract cho phép thay bằng S3/R2 mà không đổi Product service hay UI.
- Chấp nhận JPEG, PNG, WebP; xác thực bằng magic bytes thay vì tin `Content-Type`/đuôi file. Tối đa 8 MiB/file, tên lưu UUID bất biến.
- Media public đi qua `GET /api/v1/media/{**key}`; key bị giới hạn vào namespace `products/{productId}/...`, chống path traversal.
- Không giữ transaction PostgreSQL trong lúc ghi filesystem. File được lưu trước, sau đó `AddImageAsync` khóa Product `FOR UPDATE` và re-check `DRAFT`; nếu DB từ chối thì file vừa lưu được dọn ngay.
- Xóa DB commit trước, sau đó dọn managed file. External URL cũ vẫn được hỗ trợ và không bị storage xóa.
- Không cần migration mới: `product_images.file_key` hiện đã là canonical public reference; storage metadata được suy ra từ file. Thay đổi schema không mang lại invariant bổ sung.

## API

- `POST /api/v1/staff/products/{id}/images/upload` — multipart `file`, `altText`, `displayOrder`.
- `PATCH /api/v1/staff/products/{id}/images/{imageId}` — sửa alt text/thứ tự.
- `PUT /api/v1/staff/products/{id}/images/order` — sắp xếp toàn bộ gallery.
- `DELETE /api/v1/staff/products/{id}/images/{imageId}` — xóa record và managed object.
- `GET /api/v1/media/{**key}` — public immutable content.

## Verification

- Integration: RBAC/CSRF, signature giả, quá dung lượng, path traversal, upload→catalog→download, delete, published guard và reorder.
- Frontend: multi-file picker/drop zone, preview, progress/error từng file, alt/order và refresh gallery.
- Docker: volume `product-media`, fresh stack upload/download, restart API vẫn còn ảnh.
- Full backend/frontend gates, Compose validation và Graphify update.
