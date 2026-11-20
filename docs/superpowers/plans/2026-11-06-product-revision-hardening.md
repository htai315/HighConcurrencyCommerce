# T47.1 — Product Revision Hardening & Review Workspace

Ngày lập: 06/11/2026  
Trạng thái: **Hoàn tất — release gate đạt ngày 06/11/2026**

## 1. Mục tiêu

Đóng hoàn toàn T47 bằng ba lớp bảo đảm còn thiếu:

1. Admin thấy diff có cấu trúc giữa nội dung live và snapshot đề xuất, thay vì chỉ thấy tiêu đề mới và số lượng ảnh/thông số.
2. Chứng minh approval rollback nguyên tử bằng fault-injection tại từng checkpoint của transaction.
3. Cleanup ảnh revision bị xóa/reject/cancel trở thành công việc bền vững có retry, không để orphan âm thầm hoặc lịch sử trỏ tới preview đã bị xóa mà UI không biết.

Sau T47.1, `docs/TASKS.md` phải chuyển T47 từ “implementation chính” sang “Hoàn tất”, trước khi mở T48 Shipping Methods & Fees.

## 2. Hiện trạng đã kiểm chứng

- `PostgresProductRevisionService.ApproveAsync` khóa `Product -> Revision`, kiểm tra `base_content_version`, áp scalar rồi gọi `ApplyImages` và `ApplySpecifications`, thêm Audit Log và `SaveChanges` một lần.
- Backend hiện chỉ trả `ProductRevisionDto`; Admin frontend tự ghép `productTitle`, tiêu đề mới và các con số tổng hợp.
- Ảnh snapshot có `source_image_id`, đủ để đối chiếu ảnh live với ảnh đề xuất.
- Specification có `spec_key`, đủ làm identity ổn định để tính added/removed/modified.
- Reject/cancel hiện commit terminal state trước rồi gọi `SafeDelete` tuần tự. Nếu storage delete lỗi, hệ thống chỉ log warning và không có retry.
- Xóa ảnh mới khỏi revision DRAFT hiện xóa DB row trước rồi gọi best-effort delete; nếu delete lỗi thì mất dấu object cần cleanup.
- T46 đã cho revision hoạt động trên `PUBLISHED` và `SUSPENDED`; approval không resume Product.

## 3. Phạm vi

### 3.1. Có trong T47.1

- Review DTO chứa live snapshot, proposed snapshot, structured diff và stale/lifecycle flags.
- Diff scalar: title, description, category, brand.
- Diff gallery: added, removed, modified metadata, reordered, unchanged.
- Diff specifications: added, removed, modified, unchanged.
- Admin review workspace chia tab Nội dung / Hình ảnh / Thông số.
- Stale banner và vô hiệu hóa Approve khi `content_version` không còn khớp.
- Durable media cleanup queue + worker lease/retry.
- Fault-injection checkpoints và rollback tests.
- Concurrency tests với T46 lifecycle.
- API documentation, ADR, release gate, Docker E2E và Graphify.

### 3.2. Không có trong T47.1

- Không merge revision stale tự động.
- Không chỉnh sửa revision ngay trong màn hình Admin.
- Không cho nhiều revision mở song song.
- Không version SKU, giá hoặc tồn kho.
- Không schedule approval.
- Không xóa binary của ảnh từng là live khỏi storage; ảnh live bị gỡ tiếp tục tuân theo retention policy ADR 0010.
- Không xây hệ thống object-storage garbage collector tổng quát cho toàn dự án.
- Không thay đổi state machine `DRAFT -> PENDING_REVIEW -> APPROVED/REJECTED/CANCELLED`.

## 4. Invariants bắt buộc

1. Diff chỉ là projection; gọi endpoint review không được mutate Product hoặc Revision.
2. Diff luôn so sánh **live hiện tại** với **proposed snapshot hiện tại**.
3. `isStale = revision.base_content_version != product.content_version` được tính từ source-of-truth backend.
4. Admin UI không tự quyết định approval eligibility; backend vẫn revalidate dưới Product lock.
5. Approval lỗi tại bất kỳ checkpoint nào không được thay đổi Product scalar, gallery, specifications, `content_version`, revision status hoặc Audit Log.
6. Approval thành công tăng `content_version` đúng một lần và ghi đúng một Audit Log.
7. Cleanup object mới không chạy trong transaction DB và không giữ row lock khi gọi storage.
8. Reject/cancel/delete ảnh enqueue cleanup trong cùng transaction với mutation DB; commit thất bại không tạo cleanup giả.
9. Cleanup delivery là at-least-once; `IProductMediaStorage.DeleteAsync` phải được coi là idempotent khi object không còn tồn tại.
10. Hai worker không được xử lý đồng thời cùng cleanup job.
11. Job thất bại được retry bằng exponential backoff có trần và error code đã sanitize; không lưu raw exception message.
12. Revision history đã terminal vẫn hiển thị metadata. Preview ảnh mới đã cleanup phải hiện “Đã dọn tệp”, không render URL hỏng.
13. Ảnh revision được approve thành live tuyệt đối không được enqueue cleanup.
14. Product `SUSPENDED` có thể approve revision nhưng không tự chuyển `PUBLISHED`.
15. Product `ARCHIVED` không thể approve; archive vẫn bị T46 chặn khi còn revision mở.

## 5. Structured review contract

### 5.1. Endpoint

Thêm endpoint Admin-only:

```http
GET /api/v1/admin/product-revisions/{revisionId}/review
```

Endpoint GET revision cũ vẫn giữ để tương thích. Review endpoint là contract chính cho workspace Admin.

### 5.2. DTO tổng

```csharp
public sealed record ProductRevisionReviewDto(
    Guid RevisionId,
    Guid ProductId,
    int RevisionNo,
    string RevisionStatus,
    string ProductPublicationStatus,
    long BaseContentVersion,
    long CurrentContentVersion,
    bool IsStale,
    bool CanApprove,
    IReadOnlyList<ScalarRevisionDiffDto> ContentDiffs,
    IReadOnlyList<ImageRevisionDiffDto> ImageDiffs,
    IReadOnlyList<SpecificationRevisionDiffDto> SpecificationDiffs);
```

`CanApprove` chỉ là server hint cho UI, không thay thế validation trong `ApproveAsync`:

```text
revisionStatus == PENDING_REVIEW
AND !isStale
AND productPublicationStatus IN (PUBLISHED, SUSPENDED)
```

### 5.3. Scalar diff

Mỗi dòng:

```csharp
record ScalarRevisionDiffDto(
    string Field,
    string Label,
    string? CurrentValue,
    string? ProposedValue,
    bool Changed);
```

Fields ổn định: `title`, `description`, `category`, `brand`. Category/Brand trả cả ID và display name trong value object hoặc DTO riêng; không buộc frontend tự join master data.

So sánh:

- Title: ordinal sau normalization đã lưu.
- Description: `null` và chuỗi rỗng được xem tương đương theo normalization service hiện tại.
- Category/Brand: so sánh ID; render tên từ DB.

### 5.4. Gallery diff

Identity:

- Ảnh snapshot có `source_image_id`: ghép với live image ID.
- `source_image_id == null`: `ADDED`.
- Live image không còn trong proposed source set: `REMOVED`.

Change kinds:

```text
ADDED | REMOVED | MODIFIED | REORDERED | UNCHANGED
```

`MODIFIED` nếu đổi `file_key`, `alt_text` hoặc `image_status`. `REORDERED` nếu chỉ đổi `display_order`. Nếu vừa đổi metadata vừa đổi order, trả `MODIFIED` và liệt kê `changedFields` bao gồm `displayOrder`.

DTO phải chứa current/proposed metadata, `isNewUpload`, `previewAvailable` và cleanup status nếu có.

Thứ tự trả về ổn định:

1. Proposed images theo `display_order`, `id`.
2. Removed images theo live `display_order`, `id`.

### 5.5. Specification diff

Ghép theo `spec_key` đã normalize:

```text
ADDED | REMOVED | MODIFIED | UNCHANGED
```

`MODIFIED` nếu đổi display name, value hoặc display order. Trả `changedFields` để UI highlight đúng ô.

### 5.6. Stale semantics

Revision không lưu toàn bộ “base before edit” độc lập; vì vậy T47.1 không tuyên bố three-way diff. Khi stale:

- Review vẫn hiển thị **current live vs proposed**.
- Banner nói rõ live đã đổi từ version gốc.
- Approve bị disable ở UI và backend tiếp tục trả `409 REVISION_STALE`.
- Staff phải cancel/reject revision cũ rồi tạo snapshot mới; không tự rebase.

## 6. Durable media cleanup — migration `017_product_revision_cleanup.sql`

### 6.1. Bảng cleanup job

```sql
store.product_media_cleanup_jobs
```

Các cột:

- `id uuid PK`
- `product_id uuid NOT NULL REFERENCES products ON DELETE RESTRICT`
- `revision_id uuid NULL REFERENCES product_revisions ON DELETE SET NULL`
- `storage_key varchar(700) NOT NULL`
- `public_path text NULL`
- `cleanup_reason text NOT NULL CHECK IN ('REVISION_IMAGE_DELETED','REVISION_REJECTED','REVISION_CANCELLED','UPLOAD_COMPENSATION')`
- `job_status text NOT NULL CHECK IN ('PENDING','PROCESSING','COMPLETED')`
- `attempt_count integer NOT NULL DEFAULT 0 CHECK >= 0`
- `next_attempt_at timestamptz NOT NULL DEFAULT now()`
- `locked_at timestamptz NULL`
- `locked_by uuid NULL`
- `last_error_code varchar(64) NULL`
- `completed_at timestamptz NULL`
- timestamps chuẩn.

Indexes:

- Queue: `(job_status, next_attempt_at, created_at, id)` cho `PENDING/PROCESSING`.
- Partial unique `(storage_key)` khi job chưa `COMPLETED`, chống enqueue trùng đang mở.
- Index `revision_id` để review projection tìm trạng thái cleanup.

### 6.2. Enqueue semantics

- `DeleteImageAsync` của ảnh `is_new_upload=true`: xóa revision-image row và enqueue job trong cùng transaction.
- Reject/cancel: enqueue mọi ảnh `is_new_upload=true` trước khi commit terminal state.
- Ảnh source/live không enqueue.
- Dùng `INSERT ... ON CONFLICT ... DO NOTHING` hoặc guard tương đương để replay không nhân job.
- Upload compensation sau DB failure cố xóa ngay; nếu xóa thất bại và DB còn truy cập được thì enqueue `UPLOAD_COMPENSATION` trong transaction ngắn mới.

### 6.3. Worker protocol

`ProductMediaCleanupWorker` dùng short lease:

1. Claim tối đa 10 job bằng `FOR UPDATE SKIP LOCKED`, gán `PROCESSING`, `locked_at`, `locked_by`, commit ngay.
2. Gọi storage delete ngoài transaction, timeout từng item.
3. Persist từng outcome trong transaction ngắn:
   - Thành công hoặc object đã mất: `COMPLETED`, `completed_at`, clear lease.
   - Thất bại: `PENDING`, tăng attempt, exponential backoff + jitter, clear lease.
4. Lease quá hạn được reclaim.

Không giữ batch transaction trong lúc I/O. Error code chỉ lấy loại exception/status chuẩn hóa, không lưu path nội bộ hoặc message nhạy cảm.

### 6.4. Review projection sau cleanup

- Với ảnh terminal/new upload, join cleanup job bằng revision + public path/storage key.
- `previewAvailable=false` khi cleanup `COMPLETED`.
- `PENDING/PROCESSING` hiển thị “Đang dọn tệp”; UI không phụ thuộc object còn đọc được hay không.
- Revision approved chuyển ảnh thành live (`is_new_upload=false`), không có job cleanup.

## 7. Approval transaction và fault-injection

### 7.1. Test seam

Thêm interface nội bộ qua DI, production dùng no-op:

```csharp
public interface IProductRevisionApprovalCheckpoint
{
    Task ReachAsync(ProductRevisionApprovalStage stage, CancellationToken ct);
}

public enum ProductRevisionApprovalStage
{
    AfterScalarMutation,
    AfterImageMutation,
    AfterSpecificationMutation,
    AfterSaveBeforeCommit
}
```

Checkpoint chỉ phục vụ observability/test; không chứa business logic và không được cấu hình từ request/user input.

### 7.2. Approval sequence chuẩn

```text
BEGIN
  lock Product
  lock Revision
  load children
  revalidate state/version/lifecycle/taxonomy
  mutate scalar + content_version
  checkpoint AfterScalarMutation
  apply gallery
  checkpoint AfterImageMutation
  apply specifications
  checkpoint AfterSpecificationMutation
  mark APPROVED + add audit
  SaveChanges
  checkpoint AfterSaveBeforeCommit
COMMIT
```

Mỗi injected exception phải làm transaction dispose/rollback. Test dùng DbContext mới để xác minh, không đọc từ change tracker đã bẩn.

### 7.3. Snapshot assertion sau failure

Trước mỗi test lưu:

- Product title/description/category/brand/content_version/status.
- Live image IDs, file keys, alt text, display order/status.
- Live spec keys/names/values/order.
- Revision status/review fields.
- Số Audit Log `PRODUCT_REVISION_APPROVED`.

Sau failure mọi giá trị phải byte-for-byte/logically tương đương snapshot trước.

## 8. Concurrency với T46

### 8.1. Approve ↔ Suspend

Cả hai khóa Product. Legal outcomes:

- Approve trước, Suspend sau: content mới + `SUSPENDED`.
- Suspend trước, Approve sau: content mới + `SUSPENDED`.

Cả hai có thể thành công; cuối cùng không bao giờ `PUBLISHED` do approval tự resume.

### 8.2. Approve ↔ Archive

Product bắt đầu `SUSPENDED`, revision `PENDING_REVIEW`:

- Archive lấy lock trước: thấy open revision và trả `409 PRODUCT_ARCHIVE_OPEN_REVISION`; Approve có thể thành công.
- Approve lấy lock trước: revision thành `APPROVED`, Archive lấy lock sau và có thể archive.

Invariant cuối:

- Không tồn tại Product `ARCHIVED` với revision còn `PENDING_REVIEW`.
- Mỗi action thành công chỉ có một Audit Log.
- Không deadlock/500.

### 8.3. Approve ↔ Approve và stale

- Giữ test exactly-once hiện tại.
- Bổ sung structured review `isStale=true` sau khi live content version đổi.
- Approve stale luôn 409; không tự refresh base version.

## 9. Backend/API changes

### 9.1. Application contracts

- `ProductRevisionReviewDto` và các diff DTO.
- `GetReviewAsync(revisionId)` trong `IProductRevisionService` hoặc reader riêng `IProductRevisionReviewReader`.
- Khuyến nghị reader riêng để projection read-only không làm service mutation phình thêm.
- Cleanup job model/repository abstraction chỉ ở Infrastructure; không lộ endpoint Customer.

### 9.2. API

- `GET /admin/product-revisions/{id}/review`.
- GET không cần CSRF nhưng yêu cầu Admin policy.
- Existing approve/reject routes giữ nguyên.
- ProblemDetails giữ `REVISION_NOT_FOUND`, `REVISION_STALE`, `REVISION_INVALID_STATE`.
- Cleanup worker không tạo public management endpoint trong V1; trạng thái được phản ánh qua review DTO và logs/health metrics.

### 9.3. Query constraints

- Dùng `AsNoTracking` cho review projection.
- Không serialize EF entity/navigation trực tiếp.
- Query category/brand names rõ ràng; tránh N+1.
- Diff tính deterministic trong application/infrastructure helper thuần, có unit tests riêng.

## 10. Frontend Admin Review Workspace

### 10.1. Layout

- Cột trái: queue, search theo product/revision, badge stale/status, submitted time.
- Cột phải: header Product + revision/version + lifecycle badge.
- Tabs:
  1. `Nội dung`
  2. `Hình ảnh`
  3. `Thông số`
- Toggle “Chỉ hiện thay đổi” mặc định bật.

### 10.2. Content diff

- Bảng hai cột “Đang công khai” và “Đề xuất”.
- Highlight chỉ ô thay đổi, không tô cả màn hình.
- Description dài có collapse/expand; giữ text thuần, không render HTML tùy ý.
- Category/Brand hiện tên, ID chỉ là secondary metadata.

### 10.3. Image diff

- Grid paired cards với badge `Thêm`, `Gỡ`, `Đổi metadata`, `Đổi thứ tự`.
- Preview current/proposed; ảnh cleanup hiển thị placeholder có trạng thái.
- Alt text và display order được đọc bằng screen reader.
- Không dựa màu duy nhất để biểu diễn change kind.

### 10.4. Specification diff

- Table theo `spec_key`.
- Highlight display name/value/order thay đổi.
- Added/removed giữ hai phía rõ ràng.

### 10.5. Decision UX

- Banner stale/lifecycle ở đầu workspace.
- Disable Approve nếu `canApprove=false`; ghi lý do cụ thể.
- Reject bắt buộc lý do như contract hiện tại.
- Khi API trả 409, reload review và queue; không giữ optimistic success.
- Sau approve/reject thành công, remove item khỏi queue và chọn item kế tiếp.
- Focus management: sau load đưa focus vào review heading; dialog trả focus về nút mở.

## 11. Test plan

### 11.1. Diff calculator unit/integration tests

1. Scalar unchanged/changed/null normalization.
2. Category/brand name and ID changes.
3. Image added.
4. Image removed.
5. Alt/file/status modified.
6. Pure reorder.
7. Modified + reorder returns changed fields deterministically.
8. Specification added/removed/modified/unchanged.
9. Stable ordering independent of database enumeration.
10. Stale and `canApprove` flags for PUBLISHED/SUSPENDED/ARCHIVED.

### 11.2. Fault-injection tests

Parameterized test cho cả bốn checkpoint:

- API/service fails.
- Product scalar/version unchanged.
- Gallery unchanged.
- Specs unchanged.
- Revision remains `PENDING_REVIEW`.
- Không có approval audit.
- Retry không fault sau đó approve thành công exactly once.

### 11.3. Cleanup worker tests

- Reject/cancel enqueue đúng ảnh mới, không enqueue source image.
- Delete draft image enqueue trước khi mất DB reference.
- Hai worker claim batch rời nhau, không double delete.
- Storage timeout không giữ DB transaction/row lock.
- Retry backoff và expired lease recovery.
- Object already missing được coi là success.
- Approve không enqueue ảnh đã trở thành live.
- Review DTO báo preview unavailable sau completed cleanup.

### 11.4. Concurrency tests

- Approve ↔ Suspend.
- Approve ↔ Archive.
- Approve ↔ Approve regression.
- Stale version ↔ approve.
- Reject/cancel ↔ cleanup worker claim.

### 11.5. Frontend tests

- Render scalar/image/spec diff đúng change kinds.
- Toggle unchanged.
- Stale disables Approve.
- Suspended permits Approve with warning; archived blocks.
- Cleanup placeholders.
- 409 refresh queue/review.
- Keyboard tabs/focus and accessible labels.

### 11.6. Playwright flow 13

1. Staff tạo revision cho Product published.
2. Đổi title/description, thêm ảnh, xóa/reorder ảnh, thêm/sửa/xóa spec.
3. Submit.
4. Admin mở review, kiểm tra từng tab diff.
5. Suspend Product trong lúc revision chờ duyệt.
6. Admin approve; Product vẫn suspended và Catalog vẫn 404.
7. Resume; Catalog hiển thị đúng snapshot mới.
8. Tạo revision thứ hai, reject; worker cleanup ảnh mới và review history báo đã dọn tệp.

## 12. Chia task triển khai

| Bước | Hạng mục | Acceptance |
|---|---|---|
| T47.1.1 | ADR 0012 + migration 017 | Cleanup queue schema/reapply/migration gate pass |
| T47.1.2 | Diff contracts + calculator | Deterministic scalar/image/spec unit tests pass |
| T47.1.3 | Review reader + Admin API | Live/proposed/stale/lifecycle projection đúng |
| T47.1.4 | Durable cleanup enqueue + worker | Short lease, retry, no DB lock during storage I/O |
| T47.1.5 | Approval checkpoints | Four rollback fault tests pass |
| T47.1.6 | T46 concurrency hardening | Approve/Suspend/Archive legal outcomes only |
| T47.1.7 | Admin review workspace | Three diff tabs, stale guard, accessibility |
| T47.1.8 | Playwright flow 13 | Full Staff → Admin → lifecycle → cleanup flow pass |
| T47.1.9 | Docs/release gate/Graphify | T47 marked complete; all gates green |

## 13. Release gate

Chỉ đóng T47/T47.1 khi:

- Full backend suite pass trên PostgreSQL thật.
- Full frontend suite, oxlint và production build pass.
- Migration 017 chạy từ DB trống, reapply và legacy volume.
- Bốn fault-injection checkpoints chứng minh rollback.
- Cleanup concurrency/lease tests pass.
- Playwright flows 11, 12 và 13 pass tuần tự trên Docker.
- Không có revision terminal render URL ảnh cleanup như preview còn hợp lệ.
- API docs, ADR, TASKS, README cập nhật.
- Chạy `graphify update .` sau thay đổi code cuối cùng.

## 14. Rủi ro và kiểm soát

- Diff có thể lớn với gallery/spec dài: response giới hạn bởi Product media/spec limits hiện tại; không trả binary/base64.
- Cleanup at-least-once có thể gọi delete nhiều lần: storage adapter phải coi not-found là success.
- Hook fault-injection không được trở thành feature flag production: implementation no-op cố định, test thay qua DI.
- `AfterSaveBeforeCommit` failure để DbContext ở trạng thái tracked bẩn: test và retry phải dùng scope/DbContext mới.
- Stale revision không có three-way base snapshot: UI phải ghi đúng “live hiện tại vs đề xuất”, không gọi là merge conflict chi tiết.
- Worker cleanup không thay thế retention của ảnh live; chỉ xử lý object chưa từng được approve.

## 15. Công việc kế tiếp

Sau khi T47.1 đóng hoàn toàn, task nghiệp vụ mới đề xuất là **T48 — Shipping Methods & Fees**: phương thức giao hàng, bảng phí, ngưỡng miễn phí, snapshot phí vào order và chính sách refund/voucher liên quan.
