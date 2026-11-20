# T46 — Product Lifecycle Management

Ngày lập: 06/11/2026  
Trạng thái: **Đã triển khai và nghiệm thu 06/11/2026**

## 1. Mục tiêu

Hoàn thiện vòng đời của Product sau khi đã công bố: Admin có thể tạm ngừng bán khẩn cấp, mở bán lại và lưu trữ vĩnh viễn mà không xóa dữ liệu lịch sử. Catalog, Cart, Checkout, Flash Sale, Wishlist, Review, Reporting và T47 Product Revision phải nhìn cùng một trạng thái nghiệp vụ, kể cả khi request chạy đồng thời.

Khoảng trống hiện tại:

- Schema chỉ chấp nhận `DRAFT`, `PUBLISHED`, `ARCHIVED` nhưng chưa có API chuyển sang `ARCHIVED`.
- Không có trạng thái tạm ngừng bán có thể khôi phục.
- Checkout khóa SKU và tồn kho nhưng chưa khóa Product, nên một thao tác ngừng bán mới nếu chỉ cập nhật Product sẽ có race với Checkout.
- T47 hiện chỉ cho tạo revision từ `PUBLISHED`, trong khi sản phẩm tạm ngừng bán vẫn cần được sửa nội dung trước khi mở lại.

## 2. Quyết định phạm vi

### 2.1. Có trong T46

- Thêm trạng thái `SUSPENDED` cho Product.
- Admin suspend `PUBLISHED -> SUSPENDED` với lý do bắt buộc.
- Admin resume `SUSPENDED -> PUBLISHED` sau khi tái kiểm tra điều kiện bán.
- Admin archive `SUSPENDED -> ARCHIVED` với lý do bắt buộc.
- Audit Log bất biến cho mọi transition.
- Catalog, Cart, Checkout, Flash Sale public projection, Wishlist, Review và Reporting đồng bộ theo trạng thái.
- T47 cho phép soạn/duyệt revision khi Product là `PUBLISHED` hoặc `SUSPENDED`; approve không tự resume.
- Concurrency guard bằng Product row lock và lock order thống nhất.
- UI quản trị vòng đời trong Admin Dashboard; Staff chỉ xem trạng thái và tiếp tục soạn revision theo quyền hiện tại.
- Integration tests, frontend tests và Playwright chạy trên Docker thật.

### 2.2. Không có trong T46

- Không xóa cứng Product, SKU, ảnh, review, wishlist, cart item hoặc order history.
- Không tự động đổi trạng thái SKU khi suspend/archive.
- Không hoàn quota Flash Sale, Voucher, Inventory allocation hoặc đơn đã tạo.
- Không tự động kết thúc cả Flash Sale Campaign khi một Product bị suspend; một Campaign có thể chứa nhiều Product.
- Không schedule suspend/resume trong tương lai.
- Không cho Staff thay đổi publication status.
- Không đưa Product `ARCHIVED` trở lại bán trong phiên bản đầu; archive là terminal.
- Không thay đổi nội dung Product live; nội dung sau publish tiếp tục đi qua T47.

## 3. State machine

```text
DRAFT --publish--> PUBLISHED --suspend--> SUSPENDED --resume--> PUBLISHED
                                  |
                                  +--archive--> ARCHIVED (terminal)
```

Transition hợp lệ:

| Từ | Hành động | Sang | Ghi chú |
|---|---|---|---|
| `DRAFT` | Publish | `PUBLISHED` | Luồng T12 hiện có |
| `PUBLISHED` | Suspend | `SUSPENDED` | Lý do bắt buộc, hiệu lực tức thời tại ranh giới transaction |
| `SUSPENDED` | Resume | `PUBLISHED` | Revalidate SKU/giá và các điều kiện công bố |
| `SUSPENDED` | Archive | `ARCHIVED` | Lý do bắt buộc; không còn revision mở và không có offer đang hiệu lực |

Mọi transition khác trả `409 PRODUCT_LIFECYCLE_INVALID_TRANSITION`. Request lặp lại không được giả vờ thành công: client phải tải lại trạng thái mới nhất. Điều này giúp phát hiện thao tác UI cũ thay vì che giấu lỗi vận hành.

## 4. Business invariants

1. Chỉ Admin được mutate publication lifecycle.
2. `SUSPENDED` và `ARCHIVED` không xuất hiện trong Catalog, tìm kiếm, public Flash Sale hoặc PDP.
3. Không Checkout mới nào được commit dựa trên Product đã suspend/archive trước khi Checkout lấy Product lock.
4. Nếu Checkout giữ Product lock trước, Checkout được phép hoàn tất rồi Suspend mới commit. Đây là thứ tự tuyến tính hợp lệ.
5. Cart item và Wishlist item cũ không bị xóa; chúng chuyển sang unavailable/placeholder để Customer chủ động xử lý.
6. Thao tác thêm mới vào server Cart/Wishlist sau khi Product đã suspend/archive bị từ chối.
7. Guest cart trong `localStorage` có thể giữ item từ tab cũ; Checkout server luôn là hàng rào cuối và phải từ chối.
8. Suspend không sửa SKU status, giá, tồn kho, Flash Sale quota hoặc đơn đã tồn tại.
9. Offer Flash Sale gắn với Product suspended không xuất hiện public và không Checkout được. Nếu Product resume trước khi Campaign kết thúc, offer có thể hiệu lực lại với quota còn nguyên.
10. Resume yêu cầu ít nhất một SKU `ACTIVE`, và mọi SKU active có `regular_price > 0` như publish ban đầu.
11. Archive chỉ đi từ `SUSPENDED`, không có revision `DRAFT/PENDING_REVIEW`, và không có Flash Sale offer thuộc Campaign `ACTIVE/SCHEDULED` còn thời gian hiệu lực.
12. Product archived là terminal; dữ liệu lịch sử vẫn truy vấn được qua Orders, Returns, Reviews moderation, Audit và Reporting.
13. T47 create/submit/approve được phép với `PUBLISHED` hoặc `SUSPENDED`; approve trên `SUSPENDED` chỉ cập nhật content snapshot, không đổi publication status.
14. Approve revision trên `ARCHIVED` bị từ chối. Archive bị chặn nếu còn revision mở nên không được tạo trạng thái mồ côi.
15. Mỗi transition ghi đúng một Audit Log trong cùng transaction; rollback không để lại audit giả.

## 5. Lock order và concurrency

### 5.1. Thứ tự khóa chuẩn

Mở rộng thứ tự khóa Checkout hiện tại thành:

```text
Customer -> Voucher -> Product(s, UUID tăng dần)
         -> Flash Sale Campaign(s, UUID tăng dần)
         -> Flash Sale Offer(s, UUID tăng dần)
         -> SKU(s, UUID tăng dần)
         -> Inventory Balance(s, SKU UUID tăng dần)
```

Các luồng liên quan:

- Lifecycle: `Product FOR UPDATE` rồi đọc/revalidate dependent state; không khóa Customer.
- Cart/Wishlist mutation: `Customer FOR NO KEY UPDATE -> Product FOR KEY SHARE` rồi mới ghi item.
- Checkout: khóa toàn bộ Product theo UUID trước Campaign/Offer/SKU/Inventory.
- T47: `Product FOR UPDATE -> Revision FOR UPDATE -> children` như hiện tại.
- Publish Draft: `Product FOR UPDATE`, sau đó kiểm tra SKU; giữ tương thích với lock order mới.

Không có luồng nào được khóa SKU trước rồi quay lại khóa Product.

### 5.2. Race resolution bắt buộc

**Suspend ↔ Checkout**

- Checkout khóa Product trước: đơn được commit, sau đó Suspend thành công.
- Suspend khóa Product trước: Checkout đọc `SUSPENDED`, trả `409 PRODUCT_NOT_AVAILABLE`/`SKU_NOT_AVAILABLE`, không giữ tồn hay quota.

**Suspend ↔ Cart/Wishlist add**

- Add khóa Product trước: item có thể được lưu, sau đó Suspend; lần đọc kế tiếp hiển thị unavailable.
- Suspend khóa Product trước: Add bị từ chối.

**Suspend ↔ T47 approve**

- Hai luồng tuần tự qua Product lock và đều có thể thành công.
- Kết quả cuối là Product `SUSPENDED` với nội dung revision mới; approval tuyệt đối không resume.

**Resume ↔ Archive**

- Cùng tranh Product lock; đúng một transition hợp lệ, request còn lại nhận `409`, không có double audit.

## 6. Database — migration `016_product_lifecycle.sql`

### 6.1. Mở rộng CHECK constraint

Thay constraint publication status bằng:

```sql
CHECK (publication_status IN ('DRAFT','PUBLISHED','SUSPENDED','ARCHIVED'))
```

Migration phải idempotent theo convention của migration runner và chạy lại không làm đổi dữ liệu.

### 6.2. Metadata trạng thái

Thêm vào `store.products`:

```sql
status_changed_at timestamptz NOT NULL DEFAULT now(),
status_changed_by uuid REFERENCES store.user_accounts(id),
status_reason varchar(500)
```

Backfill:

- `status_changed_at = COALESCE(published_at, updated_at, created_at)`.
- `status_changed_by = COALESCE(published_by, created_by)`.
- `status_reason = NULL` cho dữ liệu cũ.

Không dùng bảng event riêng trong T46 vì `store.audit_logs` đã là lịch sử bất biến; ba cột trên chỉ biểu diễn trạng thái vận hành hiện tại.

### 6.3. Index

- Giữ `ix_products_public(publication_status, created_at)` hiện tại.
- Thêm index phục vụ Admin queue nếu query plan thực tế cần: `(publication_status, status_changed_at DESC)`.
- Không tạo partial index dư thừa trước khi đo `EXPLAIN`.

### 6.4. Migration runner và fixture

- Embed migration 016 trong Infrastructure và IntegrationTests.
- Tăng expected schema migration count từ 14 lên 15.
- Test fresh database, reapply, checksum và legacy upgrade.

## 7. Application contracts

Tạo namespace `Application/ProductLifecycle`:

```csharp
public sealed class SuspendProductRequest
{
    [Required, StringLength(500, MinimumLength = 5)]
    public string Reason { get; init; } = string.Empty;
}

public sealed class ArchiveProductRequest
{
    [Required, StringLength(500, MinimumLength = 5)]
    public string Reason { get; init; } = string.Empty;
}

public sealed record ProductLifecycleDto(
    Guid ProductId,
    string PublicationStatus,
    DateTime StatusChangedAt,
    Guid? StatusChangedBy,
    string? StatusReason);
```

Interface riêng `IProductLifecycleService`:

- `SuspendAsync(adminId, productId, request, correlationId, ct)`.
- `ResumeAsync(adminId, productId, correlationId, ct)`.
- `ArchiveAsync(adminId, productId, request, correlationId, ct)`.

Không nhồi lifecycle vào `IProductManagementService`; tách ranh giới giúp test lock/state machine độc lập.

## 8. API contract

Admin-only, CSRF bắt buộc:

| Method | Route | Ý nghĩa |
|---|---|---|
| `POST` | `/api/v1/admin/products/{id}/suspend` | Tạm ngừng bán |
| `POST` | `/api/v1/admin/products/{id}/resume` | Mở bán lại |
| `POST` | `/api/v1/admin/products/{id}/archive` | Lưu trữ terminal |

Response thành công: `200 ProductLifecycleDto`.

Mã lỗi chuẩn:

| HTTP | Code | Khi nào |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Lý do thiếu/quá dài |
| 404 | `PRODUCT_NOT_FOUND` | Product không tồn tại |
| 409 | `PRODUCT_LIFECYCLE_INVALID_TRANSITION` | State nguồn không hợp lệ |
| 409 | `PRODUCT_RESUME_REQUIREMENTS_NOT_MET` | Không còn SKU active hoặc giá không hợp lệ |
| 409 | `PRODUCT_ARCHIVE_OPEN_REVISION` | Còn revision mở |
| 409 | `PRODUCT_ARCHIVE_ACTIVE_FLASH_SALE` | Còn offer đang/sắp hiệu lực |
| 409 | `CONCURRENT_CONFLICT` | Deadlock/serialization/lock timeout sau retry |

RFC 7807 giữ `code`, `correlationId`, message tiếng Việt và không lộ SQL.

## 9. Backend implementation

### 9.1. `PostgresProductLifecycleService`

Mỗi mutation:

1. Begin `READ COMMITTED` transaction.
2. `SELECT * FROM store.products WHERE id = @id FOR UPDATE`.
3. Revalidate source state dưới lock.
4. Revalidate transition-specific guards.
5. Cập nhật `publication_status`, `status_changed_*`, `updated_at`.
6. Ghi Audit Log trong cùng transaction.
7. `SaveChanges` và commit trước khi trả success.

Audit actions:

- `PRODUCT_SUSPENDED`
- `PRODUCT_RESUMED`
- `PRODUCT_ARCHIVED`

Before/after chứa status, reason và timestamp; không serialize toàn entity/navigation.

### 9.2. Checkout

- Từ SKU IDs, đọc projection `sku_id -> product_id` trước khi khóa.
- Khóa Product IDs distinct theo UUID tăng dần trước Campaign/Offer/SKU.
- Sau khi đủ lock, load lại SKU + Product và kiểm tra `PUBLISHED`.
- Giữ idempotency snapshot hiện tại: request rejected được replay đúng cùng mã lỗi.
- Suspend conflict không được làm rò quota Flash Sale, voucher held hoặc inventory reservation.

### 9.3. Cart

- Server Customer Cart: sau customer lock, khóa Product liên quan trước khi chấp nhận set/merge.
- `LoadCartAsync` giữ item nhưng trả availability riêng `PRODUCT_SUSPENDED` hoặc `PRODUCT_ARCHIVED` thay vì gộp mơ hồ vào SKU.
- `ValidateAsync` trả message thao tác được: sản phẩm tạm ngừng bán hoặc đã lưu trữ.
- Guest cart không tự xóa; khi merge, item unavailable vào `issues`, không nhập vào cart server.

### 9.4. Wishlist

- Giữ placeholder cho Product suspended/archived.
- Add mới khóa `Customer -> Product` và chỉ nhận `PUBLISHED`.
- Remove luôn được phép ở mọi publication status.
- Availability mở rộng để UI phân biệt `PRODUCT_SUSPENDED` và `PRODUCT_UNAVAILABLE` nếu contract hiện tại cho phép; nếu cần tương thích, public API vẫn có thể map cả hai về `PRODUCT_UNAVAILABLE` nhưng Admin/diagnostic giữ status gốc.

### 9.5. Flash Sale

- Public campaign/offer chỉ trả offer có SKU active và Product `PUBLISHED`.
- Checkout vẫn revalidate Product dưới lock, không tin public projection.
- Suspend không đổi Campaign status/quota.
- Archive guard kiểm tra Campaign `ACTIVE/SCHEDULED` với `ends_at > now`.

### 9.6. Reviews và Reporting

- Public review listing tiếp tục ẩn khi Product không `PUBLISHED`.
- Review record không bị đổi status khi suspend/archive; moderation/history vẫn giữ.
- Customer không tạo review mới cho suspended/archived Product.
- Báo cáo lịch sử doanh thu không lọc order theo publication status.
- Báo cáo tồn kho vận hành phải tiếp tục cho Staff thấy suspended/archived SKU; chỉ storefront-oriented low-stock widget mới được lọc `PUBLISHED` và phải ghi rõ semantics.

### 9.7. T47 Product Revision

- Create revision cho `PUBLISHED` hoặc `SUSPENDED`.
- Submit revalidate Product không phải `ARCHIVED` dưới Product lock.
- Approve chỉ cho Product `PUBLISHED` hoặc `SUSPENDED`.
- Approve không thay đổi `status_changed_*` và publication status.
- Archive guard chặn khi có revision `DRAFT/PENDING_REVIEW`; Admin phải cancel/reject trước.

## 10. Frontend UX

### 10.1. Admin Dashboard

Trong workspace Product:

- Badge rõ `DRAFT`, `PUBLISHED`, `SUSPENDED`, `ARCHIVED`.
- `PUBLISHED`: CTA “Tạm ngừng bán”.
- `SUSPENDED`: CTA “Mở bán lại” và “Lưu trữ”.
- `ARCHIVED`: chỉ đọc, không có CTA phục hồi.
- Modal suspend/archive yêu cầu lý do, hiển thị tác động tới Catalog/Cart/Flash Sale.
- Resume confirmation hiển thị cảnh báo offer Flash Sale có thể hoạt động lại nếu Campaign còn hiệu lực.
- Optimistic UI không tự đoán transition; chỉ cập nhật sau response server.
- Khi 409, refresh Product và hiển thị trạng thái thật.

### 10.2. Staff Dashboard

- Hiển thị trạng thái và lý do hiện tại ở Product header.
- `SUSPENDED`: vẫn cho vào T47 revision workspace.
- `ARCHIVED`: read-only; không tạo/submit revision mới.

### 10.3. Customer UI

- Catalog/PDP tự biến mất qua API 404 sau suspend/archive.
- Cart không xóa item; card chuyển trạng thái cảnh báo và disable stepper/checkout cho item đó, vẫn cho “Xóa”.
- Wishlist giữ placeholder và nút “Bỏ lưu”; không cho thêm giỏ.
- Thông báo lỗi dùng ngôn ngữ “Sản phẩm đang tạm ngừng bán” khác với “Sản phẩm không còn kinh doanh”.

## 11. Test plan

### 11.1. Migration tests

1. Fresh DB có `SUSPENDED` và metadata.
2. Reapply 016 không đổi dữ liệu/trạng thái.
3. Migration count/checksum/downgrade guards pass.
4. Dữ liệu Product cũ backfill actor/time hợp lệ.

### 11.2. Backend integration

1. RBAC: Guest 401, Customer/Staff 403, Admin allowed.
2. `PUBLISHED -> SUSPENDED -> PUBLISHED` và audit đúng.
3. Chặn mọi transition trái state machine.
4. Resume guard SKU active/positive price.
5. Archive guard open revision và active/scheduled offer.
6. Archive terminal; historical orders/reviews/wishlist còn nguyên.
7. Suspended/archived biến mất khỏi Catalog/PDP/public Flash Sale.
8. Cart cũ vẫn còn item nhưng unavailable; add/merge mới bị chặn.
9. Wishlist cũ thành placeholder; add mới bị chặn; remove thành công.
10. Checkout sau suspend trả 409, không đổi inventory/quota/voucher counters.
11. Concurrent suspend ↔ checkout chỉ cho hai outcome tuyến tính hợp lệ, không 500/deadlock.
12. Concurrent suspend ↔ cart add giữ invariant.
13. Concurrent resume ↔ archive đúng một transition thành công và một audit tương ứng.
14. Concurrent suspend ↔ revision approve: revision có thể apply nhưng Product cuối vẫn suspended nếu suspend commit sau; không tự resume.
15. Approve revision trên archived bị chặn.

### 11.3. Frontend tests

- Action visibility theo từng status.
- Modal validation lý do.
- 409 refresh state.
- Cart/wishlist unavailable states và remove action.
- Staff revision trên suspended; archived read-only.

### 11.4. Playwright flow 12

1. Admin đăng nhập, chọn Product published và suspend.
2. Customer xác nhận Product biến mất khỏi Catalog; cart/wishlist cũ hiển thị unavailable.
3. Staff tạo/chỉnh/submit T47 revision khi Product suspended.
4. Admin approve revision; Product vẫn không public.
5. Admin resume; Product xuất hiện với nội dung revision mới.
6. Admin suspend lần nữa, xử lý revision/campaign guard nếu có, rồi archive.
7. Product không thể resume và dữ liệu đơn lịch sử vẫn hiển thị.

## 12. Chia task triển khai

| Bước | Hạng mục | Acceptance |
|---|---|---|
| T46.1 | ADR 0011 + migration 016 | State machine, metadata và migration reapply pass |
| T46.2 | EF mapping + fixture + migration runner | Fresh/legacy DB và checksum gate pass |
| T46.3 | Contracts, service, exception handler, Admin API | Transition/audit/RBAC đúng contract |
| T46.4 | Checkout + Cart lock integration | Không race tạo đơn sau suspend; không leak reservation |
| T46.5 | Catalog/Flash Sale/Wishlist/Review/Reporting | Public visibility và history semantics nhất quán |
| T46.6 | T47 integration | Suspended editable, approve không resume, archived blocked |
| T46.7 | Admin/Staff/Customer UI | Actions và unavailable states đầy đủ, accessible |
| T46.8 | Concurrency/failure tests | Race matrix không 500/deadlock; audit exactly-once |
| T46.9 | Playwright flow 12 + docs + release gate | Docker flow xanh, API/TASKS/README/Graphify cập nhật |

## 13. Release gate

Chỉ đánh dấu T46 hoàn tất khi:

- Full backend integration suite pass trên PostgreSQL thật.
- Full frontend Vitest, oxlint và production build pass.
- `docker compose config` và demo override hợp lệ.
- Migration 016 chạy từ DB trống và volume nâng cấp.
- Playwright flow 12 chạy end-to-end trên Docker.
- Có test race Suspend ↔ Checkout và Suspend ↔ T47 approve.
- Tài liệu API, ADR, TASKS, README cập nhật.
- Chạy `graphify update .` sau thay đổi code cuối cùng.

## 14. Rủi ro cần theo dõi

- Thêm Product lock vào Checkout làm tăng contention; phải lock UUID có thứ tự và đo thời gian suite/concurrency.
- Public Flash Sale projection hiện đã lọc Product `PUBLISHED` ở một số query; cần audit mọi endpoint thay vì giả định dùng chung query.
- Guest cart là client-side nên không thể có strong consistency; server Checkout là nguồn quyết định cuối.
- Archive terminal có thể gây thao tác nhầm; UI cần confirm rõ và lý do bắt buộc.
- Reporting có cả semantics storefront và operations; không áp filter publication status hàng loạt nếu chưa phân loại từng report.
