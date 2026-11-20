# Product Revision API

T47 tách nội dung đang bán khỏi nội dung Staff đang soạn. Catalog chỉ đọc bảng live; revision chỉ trở thành public sau khi Admin approve transaction thành công.

## Invariants

- Mỗi Product có tối đa một revision mở (`DRAFT` hoặc `PENDING_REVIEW`).
- Staff chỉ sửa `DRAFT`; Admin chỉ quyết định `PENDING_REVIEW`.
- `baseContentVersion` phải khớp `products.content_version` khi submit/approve.
- Lock order: Product → Revision → child rows.
- Approval áp scalar, gallery và specifications nguyên tử; không thay đổi SKU, giá, inventory hoặc publication status.
- Ảnh mới bị xóa/reject/cancel được enqueue cleanup cùng transaction; worker xóa storage ngoài transaction và retry an toàn.

## Staff endpoints

| Method | Endpoint | Kết quả |
|---|---|---|
| GET | `/api/v1/staff/products/{productId}/revisions/open` | Revision đang mở hoặc 404 |
| POST | `/api/v1/staff/products/{productId}/revisions` | Tạo snapshot từ live, 201 |
| GET | `/api/v1/staff/product-revisions/{id}` | Chi tiết snapshot |
| PATCH | `/api/v1/staff/product-revisions/{id}` | Sửa title/description/category/brand |
| POST | `/api/v1/staff/product-revisions/{id}/submit` | `DRAFT → PENDING_REVIEW` |
| POST | `/api/v1/staff/product-revisions/{id}/cancel` | Hủy revision mở |
| POST | `/api/v1/staff/product-revisions/{id}/images/upload` | Multipart upload ảnh revision |
| PATCH/DELETE | `/api/v1/staff/product-revisions/{id}/images/{imageId}` | Sửa/xóa ảnh snapshot |
| PUT | `/api/v1/staff/product-revisions/{id}/images/order` | Sắp xếp toàn gallery |
| PUT/DELETE | `/api/v1/staff/product-revisions/{id}/specifications/{key}` | Upsert/xóa thông số snapshot |

Upload ảnh dùng cùng giới hạn và magic-byte validation của [Product Media API](product-media.md).

## Admin endpoints

| Method | Endpoint | Kết quả |
|---|---|---|
| GET | `/api/v1/admin/product-revisions?status=PENDING_REVIEW` | Queue theo thời gian submit |
| GET | `/api/v1/admin/product-revisions/{id}` | Snapshot cần duyệt |
| GET | `/api/v1/admin/product-revisions/{id}/review` | Diff live/proposed có cấu trúc và approval guard |
| POST | `/api/v1/admin/product-revisions/{id}/approve` | Áp snapshot live nguyên tử |
| POST | `/api/v1/admin/product-revisions/{id}/reject` | Từ chối; `reason` bắt buộc |

Mọi mutation yêu cầu session đúng role và `X-CSRF-TOKEN`.

## Review contract

Review response gồm:

- `productStatus`, `baseContentVersion`, `currentContentVersion`, `isStale`, `canApprove`.
- `content`: title, description, category và brand với giá trị live/proposed và cờ `changed`.
- `images`: `ADDED`, `REMOVED`, `MODIFIED`, `UNCHANGED`; hai phía có đường dẫn, alt text, thứ tự, `cleanupStatus` và `previewAvailable`.
- `specifications`: ghép ổn định theo `key`, trả `ADDED`, `REMOVED`, `MODIFIED`, `UNCHANGED`.

`canApprove` chỉ là hint cho UI. Endpoint approve luôn khóa Product rồi Revision và revalidate trạng thái/version/lifecycle. Khi Product `SUSPENDED`, revision vẫn có thể được duyệt nhưng Product giữ nguyên `SUSPENDED`.

## Durable media cleanup

Migration 017 tạo `store.product_media_cleanup_jobs`. Worker claim tối đa 10 job bằng `FOR UPDATE SKIP LOCKED`, commit lease trước khi gọi storage, timeout từng item và persist kết quả riêng. Job lỗi trở về `PENDING` với exponential backoff; lease quá hạn được reclaim. Không lưu raw exception message. Binary đã không tồn tại được coi là cleanup thành công.

## Error codes

| HTTP | Code | Ý nghĩa |
|---|---|---|
| 404 | `REVISION_NOT_FOUND` | Không thấy Product/revision/child |
| 409 | `REVISION_ALREADY_OPEN` | Product đã có revision mở |
| 409 | `REVISION_INVALID_STATE` | Transition hoặc mutation sai trạng thái |
| 409 | `REVISION_STALE` | Live content version đã thay đổi |
| 409 | `REVISION_BRAND_INACTIVE` | Brand không còn được dùng lúc approve |
| 409 | `REVISION_SNAPSHOT_INVALID` | Gallery snapshot đã đổi khi reorder |
| 400 | `REVISION_CATEGORY_NOT_FOUND` | Category không tồn tại |
| 400 | `VALIDATION_ERROR` | Payload không hợp lệ |

ProblemDetails luôn có `code` và `correlationId` theo middleware chung.
