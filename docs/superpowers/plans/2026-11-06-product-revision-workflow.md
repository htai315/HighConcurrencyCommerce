# T47 — Product Revision Workflow

Ngày lập: 06/11/2026  
Trạng thái: **Đã hoàn tất implementation chính; hardening tiếp tục tại T47.1**

Kế hoạch hardening: [2026-11-06-product-revision-hardening.md](2026-11-06-product-revision-hardening.md).

## 1. Mục tiêu

Cho phép Staff soạn thay đổi nội dung cho sản phẩm đang `PUBLISHED` mà không làm thay đổi Storefront. Chỉ khi Admin duyệt, toàn bộ snapshot mới được áp dụng nguyên tử lên bản live. Trong mọi thời điểm trước commit duyệt, Customer phải tiếp tục nhìn thấy phiên bản đã công bố gần nhất.

T47 giải quyết khoảng trống hiện tại: `PostgresProductManagementService` chỉ cho sửa title/description/category/brand/images/specifications khi Product còn `DRAFT`; sau Publish sản phẩm bị đóng băng hoàn toàn.

## 2. Quyết định phạm vi

### 2.1. Có trong T47

- Revision cho `title`, `description`, `category`, `brand`.
- Snapshot đầy đủ gallery ảnh: file key, alt text, trạng thái, thứ tự.
- Snapshot đầy đủ thông số kỹ thuật.
- Staff tạo, sửa, upload ảnh, bỏ ảnh, sắp xếp và submit revision.
- Admin xem diff, approve hoặc reject kèm lý do.
- Catalog chỉ đọc bảng live hiện có; không cần join revision.
- Audit Log cho create/submit/approve/reject/cancel và thay đổi public snapshot.
- Optimistic conflict bằng `content_version` kết hợp row lock PostgreSQL.
- Cleanup ảnh upload chỉ thuộc revision khi revision bị hủy/từ chối.

### 2.2. Không có trong T47

- Không sửa SKU code, option, trạng thái SKU, giá hoặc tồn kho qua revision.
- Không thay đổi `publication_status` khi approve; T47 không thay thế lifecycle T46.
- Không schedule thời điểm publish revision.
- Không có nhiều nhánh revision song song hoặc merge tự động.
- Không xóa ngay binary của ảnh live bị loại khỏi gallery; tránh phá URL immutable/CDN đang được cache. Orphan retention/garbage collection là task vận hành riêng.
- Không đưa rich-text HTML tùy ý vào description.

Lý do loại SKU khỏi scope: SKU là định danh gắn với inventory, flash-sale, order item và báo cáo. Áp snapshot SKU như nội dung thường có thể làm đứt lịch sử hoặc tạo SKU giá 0 trên sản phẩm đang bán.

## 3. Invariants bắt buộc

1. Product live và Catalog không đổi trước khi Admin approve thành công.
2. Mỗi Product có tối đa một revision mở ở `DRAFT` hoặc `PENDING_REVIEW`.
3. Revision chỉ được tạo từ Product `PUBLISHED`. Nếu T46 đã có `SUSPENDED`, có thể cho tạo revision nhưng approve không tự mở bán lại.
4. Staff chỉ sửa revision `DRAFT`; revision `PENDING_REVIEW` là read-only.
5. Admin chỉ approve/reject revision `PENDING_REVIEW`.
6. Approve chỉ thành công khi `revision.base_content_version == product.content_version`.
7. Approve áp title/description/category/brand/gallery/specifications trong một transaction và tăng `product.content_version` đúng một lần.
8. Hai Admin approve đồng thời: đúng một request thành công; request còn lại không ghi thêm Audit Log và không áp dữ liệu lần hai.
9. Staff mutation và Admin decision cùng thứ tự khóa `Product -> Revision -> revision children`.
10. Ảnh upload mới không được gắn vào bảng live trước approve. Upload DB thất bại phải xóa object bù.
11. Reject/cancel không thay đổi Product live, SKU, inventory, price, review, wishlist hay order history.
12. Không trả success trước khi transaction commit.

## 4. State machine

```text
DRAFT --submit--> PENDING_REVIEW --approve--> APPROVED
  |                    |       |
  +--cancel----------->|       +--reject----> REJECTED
                       +--cancel-----------> CANCELLED
```

- `APPROVED`, `REJECTED`, `CANCELLED` là terminal.
- Revision bị stale khi `base_content_version` không còn khớp live. Approve trả `409 REVISION_STALE`; không tự merge.
- Sau terminal state, Staff có thể tạo revision mới từ snapshot live mới nhất.

## 5. Database — migration `015_product_revisions.sql`

### 5.1. Mở rộng Product

```sql
ALTER TABLE store.products
ADD COLUMN IF NOT EXISTS content_version bigint NOT NULL DEFAULT 1
CHECK (content_version > 0);
```

`content_version` chỉ tăng khi public content snapshot đổi; đổi giá, tồn kho hoặc trạng thái vận hành không tăng version.

### 5.2. `store.product_revisions`

Các cột chính:

- `id uuid PK`
- `product_id uuid NOT NULL FK products RESTRICT`
- `revision_no integer NOT NULL`
- `revision_status text CHECK IN ('DRAFT','PENDING_REVIEW','APPROVED','REJECTED','CANCELLED')`
- `base_content_version bigint NOT NULL`
- snapshot: `title`, `description`, `category_id`, `brand_id`
- `created_by`, `submitted_by`, `reviewed_by`
- `submitted_at`, `reviewed_at`
- `review_note varchar(1000)`
- timestamps UTC
- unique `(product_id, revision_no)`

Partial unique index:

```sql
CREATE UNIQUE INDEX ux_product_one_open_revision
ON store.product_revisions(product_id)
WHERE revision_status IN ('DRAFT','PENDING_REVIEW');
```

### 5.3. `store.product_revision_images`

- `id uuid PK`
- `revision_id uuid FK ... ON DELETE CASCADE`
- `source_image_id uuid NULL FK product_images ON DELETE SET NULL`
- `file_key text NOT NULL`
- `alt_text varchar(240)`
- `image_status ACTIVE/INACTIVE`
- `display_order integer >= 0`
- `is_new_upload boolean NOT NULL`
- timestamps

`source_image_id` cho phép giữ nguyên ID của ảnh live khi approve; ảnh upload mới được tạo `ProductImage` mới.

### 5.4. `store.product_revision_specifications`

- `id uuid PK`
- `revision_id uuid FK ... ON DELETE CASCADE`
- `spec_key varchar(80)` theo `snake_case`
- `display_name varchar(160)`
- `spec_value text`
- `display_order integer >= 0`
- unique `(revision_id, spec_key)`

Migration phải idempotent theo migration runner hiện tại, có EF mapping và fixture PostgreSQL thật.

## 6. Application contracts

Tạo module `Application/ProductRevisions`:

- `ProductRevisionDto`, `ProductRevisionSummaryDto`
- `ProductRevisionDiffDto`
- `CreateProductRevisionRequest`
- `UpdateProductRevisionRequest`
- `UpdateRevisionImageRequest`, `ReorderRevisionImagesRequest`
- `UpsertRevisionSpecificationRequest`
- `SubmitProductRevisionRequest`
- `RejectProductRevisionRequest` với reason bắt buộc
- `IProductRevisionService`
- `ProductRevisionFailure`

Diff phải có cấu trúc, không chỉ là JSON text:

- Scalar: before/after cho title, description, category, brand.
- Images: added/removed/metadataChanged/orderChanged.
- Specifications: added/removed/changed.

## 7. Backend service và transaction

### 7.1. Tạo revision

1. Mở transaction `READ COMMITTED`.
2. Khóa `store.products FOR UPDATE`.
3. Kiểm tra Product đủ điều kiện.
4. Tìm revision mở; nếu có trả `409 REVISION_ALREADY_OPEN` cùng revision ID.
5. Tính `revision_no = max + 1` dưới Product lock.
6. Copy scalar, toàn bộ ảnh live và specifications vào snapshot revision.
7. Ghi `PRODUCT_REVISION_CREATED` audit.
8. Commit rồi trả `201`.

Bắt PostgreSQL `23505` ở partial unique index làm defense-in-depth; không trả 500.

### 7.2. Staff sửa revision

Mọi mutation dùng thứ tự:

1. `products FOR UPDATE`
2. `product_revisions FOR UPDATE`
3. revision child rows theo ID ổn định khi cần

Chỉ cho trạng thái `DRAFT`. Validation category/brand tương đương Product Draft hiện tại. Không chạm bảng live.

Upload ảnh reuse `IProductMediaStorage`: ghi binary ngoài DB transaction; sau đó khóa Product -> Revision và insert snapshot image. Nếu insert thất bại hoặc revision đã submit, xóa object vừa upload bằng `CancellationToken.None`.

### 7.3. Submit

- Khóa Product -> Revision.
- Revalidate status, base version và snapshot tối thiểu.
- Chuyển `DRAFT -> PENDING_REVIEW`.
- Ghi actor/time và audit `PRODUCT_REVISION_SUBMITTED`.
- Có thể tạo in-app notification idempotent cho Admin bằng event key revision ID; notification không làm transaction approve phụ thuộc external network.

### 7.4. Approve

Trong một transaction:

1. Khóa Product -> Revision.
2. Revalidate `PENDING_REVIEW` và `base_content_version`.
3. Revalidate category tồn tại và brand còn active.
4. Cập nhật scalar live.
5. Đồng bộ gallery:
   - source image còn trong snapshot: giữ ID, cập nhật metadata/order/status;
   - source image bị bỏ: xóa metadata live nhưng không xóa binary ngay;
   - ảnh mới: insert `ProductImage` mới.
6. Đồng bộ specifications theo `spec_key` bằng upsert/delete.
7. Tăng `content_version += 1`, cập nhật `updated_at`.
8. Chuyển revision sang `APPROVED`, lưu reviewer/time.
9. Ghi một Audit Log `PRODUCT_REVISION_APPROVED` chứa revision ID, version before/after và structured diff.
10. Commit rồi mới trả success.

Không sửa `publication_status`, `published_at`, SKU price hoặc inventory.

### 7.5. Reject/Cancel

- Reject: Admin, chỉ `PENDING_REVIEW`, reason bắt buộc.
- Cancel: Staff, `DRAFT` hoặc `PENDING_REVIEW`; nếu đã submit, ghi rõ actor và lý do.
- Sau commit terminal state, xóa best-effort các object `is_new_upload=true` không được live table tham chiếu. Lỗi cleanup được log để orphan cleanup xử lý, không rollback decision đã commit.

## 8. API contract dự kiến

### Staff

- `GET /api/v1/staff/products/{productId}/revisions/open`
- `POST /api/v1/staff/products/{productId}/revisions`
- `GET /api/v1/staff/product-revisions/{revisionId}`
- `PATCH /api/v1/staff/product-revisions/{revisionId}`
- `POST /api/v1/staff/product-revisions/{revisionId}/images/upload`
- `PATCH /api/v1/staff/product-revisions/{revisionId}/images/{imageId}`
- `DELETE /api/v1/staff/product-revisions/{revisionId}/images/{imageId}`
- `PUT /api/v1/staff/product-revisions/{revisionId}/images/order`
- `PUT /api/v1/staff/product-revisions/{revisionId}/specifications/{key}`
- `DELETE /api/v1/staff/product-revisions/{revisionId}/specifications/{key}`
- `POST /api/v1/staff/product-revisions/{revisionId}/submit`
- `POST /api/v1/staff/product-revisions/{revisionId}/cancel`

### Admin

- `GET /api/v1/admin/product-revisions?status=PENDING_REVIEW&page=1&pageSize=25`
- `GET /api/v1/admin/product-revisions/{revisionId}`
- `POST /api/v1/admin/product-revisions/{revisionId}/approve`
- `POST /api/v1/admin/product-revisions/{revisionId}/reject`

Tất cả mutation dùng CSRF. Staff/Admin isolation theo policy hiện tại. Không có anonymous revision endpoint.

## 9. Error contract

- `REVISION_NOT_FOUND` — 404
- `REVISION_ALREADY_OPEN` — 409
- `REVISION_INVALID_STATE` — 409
- `REVISION_STALE` — 409
- `REVISION_SNAPSHOT_INVALID` — 400
- `REVISION_CATEGORY_NOT_FOUND` — 400/404 theo contract thống nhất
- `REVISION_BRAND_INACTIVE` — 409
- `MEDIA_EMPTY`, `MEDIA_TOO_LARGE`, `MEDIA_UNSUPPORTED`, `MEDIA_TYPE_MISMATCH` — 400
- `CONCURRENT_CONFLICT` — 409 sau retry hữu hạn cho `40P01/40001`, nếu cần
- Model validation — `VALIDATION_ERROR` 400

ProblemDetails phải có `code` và `correlationId`, không lộ SQL/path vật lý.

## 10. Frontend

### Staff workspace

- Sản phẩm `PUBLISHED` có CTA “Tạo bản chỉnh sửa”.
- Hiển thị badge `Bản nháp chỉnh sửa` hoặc `Đang chờ duyệt`.
- Editor dùng lại form content/media/spec hiện có nhưng gọi revision API.
- Split preview “Đang công khai” và “Bản đề xuất”.
- Diff summary trước Submit.
- Disable toàn bộ editor khi `PENDING_REVIEW`.
- Confirm dialog cho submit/cancel; xử lý 409 stale/open rõ ràng.

### Admin workspace

- Tab “Duyệt thay đổi sản phẩm” có queue FIFO, filter trạng thái.
- Diff theo từng trường; ảnh added/removed/reordered hiển thị trực quan.
- Approve và Reject bằng confirm dialog; Reject bắt buộc nhập lý do.
- Sau quyết định, item biến mất khỏi pending queue và hiển thị trạng thái terminal.

### Storefront

- Không cần loader/API revision.
- Test chứng minh trước approve vẫn render live cũ; sau approve render snapshot mới sau reload/revalidation.

## 11. Kế hoạch kiểm thử

### Backend integration — PostgreSQL thật

1. RBAC: Guest/Customer bị chặn; Staff không approve; Admin không sửa draft revision.
2. Tạo revision copy đúng snapshot live và không đổi Catalog.
3. Partial unique: hai Staff tạo đồng thời -> một `201`, một `409`, không 500.
4. Staff cập nhật scalar/spec/image revision; live tables không đổi.
5. Upload giả MIME/magic bị chặn và không tạo row.
6. Submit làm revision read-only.
7. Approve áp toàn bộ snapshot nguyên tử, tăng version đúng một lần, Catalog đổi sau commit.
8. Reject/cancel giữ nguyên live và cleanup ảnh upload mới.
9. Hai Admin approve đồng thời -> đúng một success, đúng một audit approval.
10. Staff edit/upload đồng thời Admin approve -> lock order bảo toàn snapshot; mutation hoặc đi trước hoàn toàn hoặc nhận invalid state.
11. Product live version đổi trước approve -> `409 REVISION_STALE`, không partial apply.
12. Brand bị deactivate/category bị xóa giữa submit và approve -> approve bị chặn an toàn.
13. Transaction fail giữa apply images/specs -> rollback toàn bộ scalar và children.
14. Revision không thay đổi SKU ID, price, inventory, Flash Sale hay order history.
15. Migration chạy lại idempotent và giữ dữ liệu revision.

### Frontend tests

- Tạo revision, edit và submit.
- Live/revision diff đúng.
- PENDING editor disabled.
- Admin approve/reject và reason validation.
- Stale/409 hiển thị hướng dẫn reload, không optimistic success sai.
- Upload/reorder ảnh revision.

### Playwright E2E flow 11

1. Staff login, chọn sản phẩm Published.
2. Tạo revision, đổi title/description, upload ảnh và sửa thông số.
3. Mở Storefront bằng context Guest: vẫn thấy live cũ.
4. Staff submit.
5. Admin xem structured diff và approve.
6. Guest reload: thấy title/spec/gallery mới.
7. Kiểm tra SKU/giá/tồn kho không đổi.

## 12. Trình tự triển khai

| Bước | Hạng mục | Điều kiện hoàn thành |
|---|---|---|
| T47.1 | ADR 0010 + migration 015 | Chốt snapshot model, state machine, lock order, migration reapply pass |
| T47.2 | EF models/mapping/fixture | Quan hệ và partial unique index khớp PostgreSQL |
| T47.3 | Application contracts + exception handler | DTO validation và ProblemDetails ổn định |
| T47.4 | Staff revision service/API | Create/edit/media/spec/submit/cancel không chạm live |
| T47.5 | Admin review service/API | Diff/approve/reject transaction nguyên tử |
| T47.6 | Concurrency & failure tests | Race create/approve/edit, stale version, rollback pass |
| T47.7 | React Staff/Admin UI | Editor + diff + review queue hoàn chỉnh |
| T47.8 | Catalog regression + Playwright flow 11 | Live cũ trước approve, live mới sau approve |
| T47.9 | Docs/release gate/Graphify | API, TASKS, README, full suite, Docker và graph cập nhật |

## 13. Definition of Done

- Không có thay đổi public nào trước approval commit.
- Không có partial apply giữa scalar, gallery và specifications.
- Một Product không có hai revision mở.
- Concurrency tests chứng minh approve exactly-once và không deadlock.
- Media mới được cleanup đúng khi thất bại/reject/cancel.
- Audit đủ actor, revision ID, version và before/after.
- Backend integration, frontend test, lint, build, Compose và Playwright flow 11 đều pass.
- Chạy `graphify update .` sau mọi code change trước bàn giao.
