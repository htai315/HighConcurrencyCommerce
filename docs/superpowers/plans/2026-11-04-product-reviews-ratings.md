# Implementation Plan — T36: Product Reviews & Ratings

Ngày chốt kế hoạch: **04/11/2026**  
Trạng thái: **Kế hoạch đã chốt — chưa triển khai**  
Phụ thuộc: T12 Product Catalog, T25 Fulfillment, T31 Customer Order Center, T35 E2E & Release Gate.  
Task tiếp theo: T37 Wishlist → T38 Basic Vouchers. PayOS T19–T22 tiếp tục deferred theo ADR 0003.

---

## 1. Mục tiêu

T36 bổ sung đánh giá sản phẩm có xác nhận mua hàng, gồm:

- Customer đã mua và nhận hàng thành công được đánh giá sản phẩm từ 1–5 sao kèm nhận xét.
- Public catalog hiển thị điểm trung bình, tổng lượt đánh giá, phân bố sao và danh sách nhận xét.
- Customer quản lý review của chính mình; owner isolation tuyệt đối.
- Staff/Admin ẩn hoặc khôi phục nội dung vi phạm và mọi quyết định moderation được audit.
- Aggregate rating luôn đúng dưới concurrency, không dùng counter dễ lệch.
- Có integration tests, frontend tests và Playwright E2E chạy trên Docker demo stack thật.

## 2. Quyết định nghiệp vụ đã chốt

### 2.1 Điều kiện được đánh giá

Customer chỉ đủ điều kiện khi tồn tại ít nhất một `sales_order_item` thỏa đồng thời:

1. `sales_orders.customer_id` là Customer hiện tại.
2. `sales_orders.order_status = 'COMPLETED'`.
3. Shipment của đơn có `shipment_status = 'DELIVERED'` và `delivered_at IS NOT NULL`.
4. SKU của order item thuộc đúng Product cần đánh giá.

Không dựa riêng vào trạng thái hiển thị trên frontend. Service phải kiểm tra lại toàn bộ điều kiện trong PostgreSQL.

### 2.2 Số lượng review và khả năng chỉnh sửa

- Tối đa **một review đang hoạt động cho mỗi cặp `(customer_id, product_id)`**.
- Review được tạo tự động ở trạng thái `PUBLISHED`; chưa triển khai pre-moderation queue.
- Customer được sửa rating/nội dung review của mình.
- Customer được xóa mềm review của mình. Sau khi xóa, Customer có thể tạo review mới; bản ghi cũ vẫn giữ để audit dữ liệu.
- Nếu review đang `HIDDEN`, Customer sửa nội dung thì review vẫn `HIDDEN`; chỉnh sửa không được tự động công khai lại.
- Customer không được thay đổi Product, Customer hoặc qualifying order item của review hiện có.

### 2.3 Nội dung và validation

- `rating`: số nguyên 1–5, bắt buộc.
- `content`: bắt buộc, trim, từ 10–2.000 ký tự.
- Chỉ lưu plain text; không nhận HTML/Markdown thực thi. React render text bình thường để chống stored XSS.
- Không lưu ảnh/video review, reply, helpful vote hoặc automated profanity detection trong T36.

### 2.4 Hiển thị công khai và riêng tư

- Chỉ review `PUBLISHED` và `deleted_at IS NULL` được public và được tính aggregate.
- Public DTO tuyệt đối không trả email, phone hoặc `customer_id`.
- Tên người review trả về dưới dạng `reviewerDisplayName` đã mask ở server.
- Mọi review hợp lệ đều có `verifiedPurchase = true`, được chứng minh bằng `qualifying_order_item_id`.
- Review của Product `DRAFT`/`ARCHIVED` không xuất hiện trên public catalog.

### 2.5 Moderation

- Cả role `STAFF` và `ADMIN` được dùng policy mới `ReviewModerator`, chỉ áp dụng cho module review.
- Moderator có thể `HIDE` review công khai với lý do bắt buộc từ 3–500 ký tự.
- Moderator có thể `RESTORE` review đã ẩn; lý do khôi phục là tùy chọn, tối đa 500 ký tự.
- Mọi transition khóa review row bằng `FOR UPDATE`, revalidate trạng thái dưới khóa và ghi `audit_logs` trong cùng transaction.
- Audit action: `PRODUCT_REVIEW_HIDDEN`, `PRODUCT_REVIEW_RESTORED`.

## 3. Thiết kế dữ liệu

Tạo migration `database/008_product_reviews.sql`:

```sql
CREATE TABLE store.product_reviews (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    customer_id uuid NOT NULL REFERENCES store.user_accounts(id),
    product_id uuid NOT NULL REFERENCES store.products(id),
    qualifying_order_item_id uuid NOT NULL REFERENCES store.sales_order_items(id),
    rating smallint NOT NULL CHECK (rating BETWEEN 1 AND 5),
    content varchar(2000) NOT NULL CHECK (char_length(btrim(content)) BETWEEN 10 AND 2000),
    review_status text NOT NULL DEFAULT 'PUBLISHED'
        CHECK (review_status IN ('PUBLISHED', 'HIDDEN')),
    moderated_by uuid REFERENCES store.user_accounts(id),
    moderated_at timestamptz,
    moderation_reason varchar(500),
    deleted_at timestamptz,
    CONSTRAINT ck_product_review_moderation CHECK (
        (review_status = 'PUBLISHED' AND moderated_at IS NULL AND moderation_reason IS NULL)
        OR
        (review_status = 'HIDDEN' AND moderated_by IS NOT NULL
            AND moderated_at IS NOT NULL AND moderation_reason IS NOT NULL)
    )
);

CREATE UNIQUE INDEX ux_product_reviews_active_customer_product
    ON store.product_reviews(customer_id, product_id)
    WHERE deleted_at IS NULL;

CREATE INDEX ix_product_reviews_public_product
    ON store.product_reviews(product_id, created_at DESC, id)
    WHERE review_status = 'PUBLISHED' AND deleted_at IS NULL;

CREATE INDEX ix_product_reviews_moderation
    ON store.product_reviews(review_status, created_at DESC, id)
    WHERE deleted_at IS NULL;
```

Lưu ý implementation:

- Migration phải idempotent theo numbered-SQL policy hiện tại.
- Mount `008_product_reviews.sql` trong base `compose.yaml` sau migration 006; demo seed 007 vẫn chỉ nằm trong `compose.demo.yaml`.
- Embed migration trong IntegrationTests với logical name `ProductReviewsSchema.sql` và thêm vào thứ tự khởi tạo `CatalogFixture` sau schema 006.
- Cập nhật EF model thủ công: `ProductReview`, navigation trong `Product`, `UserAccount`, `SalesOrderItem`, `DbSet<ProductReview>` và Fluent mapping trong `StoreDbContext`.
- Không thêm `rating_average` hay `rating_count` vào `products`. PostgreSQL query aggregate từ source-of-truth review để loại bỏ race/lost-update counter.

## 4. Application contracts

Tạo `HighConcurrencyCommerce.Application/Reviews/ReviewContracts.cs`:

- `RatingDistributionDto(One, Two, Three, Four, Five)`.
- `ProductRatingSummaryDto(AverageRating, ReviewCount, Distribution)`.
- `PublicProductReviewDto` không chứa PII hoặc Customer ID.
- `ProductReviewPageDto` gồm items, summary, page, pageSize, totalCount.
- `MyProductReviewDto` gồm trạng thái moderation và Product summary tối thiểu.
- `ReviewEligibilityDto` gồm `eligible`, `reasonCode`, `existingReview`.
- `CreateProductReviewRequest` và `UpdateProductReviewRequest`.
- `ModerateProductReviewRequest` với action `HIDE|RESTORE` và reason.
- `ReviewQuery` với `page`, `pageSize`, `sort = newest|highest|lowest`.
- `ReviewModerationQuery` với status, search text, page/pageSize.
- `IProductReviewService` cho public/customer/moderation use cases.
- `ProductReviewFailure` và các error code chuẩn.

Error contract:

| HTTP | Code | Trường hợp |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Rating/content/query không hợp lệ |
| 401 | — | Chưa đăng nhập khi gọi Customer/Moderator API |
| 403 | — | Sai role |
| 404 | `PRODUCT_NOT_FOUND` | Product không tồn tại |
| 404 | `REVIEW_NOT_FOUND` | Không tồn tại hoặc không thuộc owner |
| 409 | `REVIEW_NOT_ELIGIBLE` | Chưa có đơn COMPLETED + DELIVERED hợp lệ |
| 409 | `REVIEW_ALREADY_EXISTS` | Đã có active review cho Product |
| 409 | `INVALID_REVIEW_STATE` | Transition moderation không hợp lệ |
| 409 | `CONCURRENT_CONFLICT` | Xung đột serialization/deadlock đã retry hết |

## 5. Infrastructure service và concurrency

Tạo `PostgresProductReviewService.cs`.

### 5.1 Create review

1. Chuẩn hóa input.
2. Mở transaction `ReadCommitted`.
3. Khóa Customer bằng `user_accounts FOR UPDATE` để tuần tự các create review cùng Customer.
4. Kiểm tra Product tồn tại và đang `PUBLISHED`.
5. Tìm qualifying order item mới nhất bằng join Order → Shipment → SKU → Product.
6. Kiểm tra chưa có active review.
7. Insert review và commit.
8. Bắt PostgreSQL `23505` của partial unique index, chuyển thành `409 REVIEW_ALREADY_EXISTS`, không để lọt 500.

### 5.2 Update/Delete owner-only

- Khóa review `FOR UPDATE` với điều kiện `customer_id` và `deleted_at IS NULL`.
- Cross-owner và ID không tồn tại đều trả `404 REVIEW_NOT_FOUND`.
- Update chỉ thay rating/content/updated_at; review `HIDDEN` giữ nguyên trạng thái.
- Delete chỉ set `deleted_at`, không hard-delete qualifying evidence.

### 5.3 Moderation

- Khóa review `FOR UPDATE`, revalidate trạng thái rồi mới chuyển `PUBLISHED ↔ HIDDEN`.
- `HIDE` ghi moderator/reason/time.
- `RESTORE` trả về `PUBLISHED` và xóa các field moderation hiện hành; audit log vẫn giữ evidence lịch sử.
- Ghi AuditLog cùng transaction; actor lấy từ session đã xác thực.
- Update/Delete/Moderate cùng khóa một row nên kết quả tuyến tính; không có trường hợp review đã xóa vẫn bị public trở lại.

### 5.4 Aggregate và query

- `AverageRating`, `ReviewCount` và bucket 1–5 lấy từ cùng predicate public.
- Average làm tròn 2 chữ số ở DTO boundary.
- Query public dùng `AsNoTracking`, deterministic ordering `sort key → created_at DESC → id`.
- Clamp page 1–100.000, pageSize 1–100 và tính offset bằng `long` để chống overflow.
- Catalog list/detail chỉ lấy average/count bằng SQL projection, không N+1 và không load toàn bộ reviews.

## 6. API design

### Public catalog

| Method | Path | Mục đích |
|---|---|---|
| GET | `/api/v1/catalog/products/{productId}/reviews` | Review công khai + summary + phân trang |

Mở rộng `ProductSummaryDto` và `ProductDetailDto` với `averageRating`, `reviewCount`.

### Customer

| Method | Path | Mục đích |
|---|---|---|
| GET | `/api/v1/products/{productId}/review-eligibility` | Eligibility + review hiện có |
| POST | `/api/v1/products/{productId}/reviews` | Tạo review |
| GET | `/api/v1/reviews/mine` | Danh sách review của tôi |
| PATCH | `/api/v1/reviews/{reviewId}` | Sửa review owner-only |
| DELETE | `/api/v1/reviews/{reviewId}` | Xóa mềm owner-only |

### Moderation

| Method | Path | Mục đích |
|---|---|---|
| GET | `/api/v1/moderation/reviews` | Queue review cho Staff/Admin |
| PATCH | `/api/v1/moderation/reviews/{reviewId}` | HIDE/RESTORE |

Tạo:

- `CatalogReviewsController` `[AllowAnonymous]`.
- `CustomerReviewsController` `[Authorize(Policy = "Customer")]`.
- `ReviewModerationController` `[Authorize(Policy = "ReviewModerator")]`.
- `ProductReviewExceptionHandler` trả RFC 7807, `code` và `correlationId`.
- Đăng ký service, policy và exception handler trong `Program.cs`.
- Mọi POST/PATCH/DELETE tiếp tục chịu CSRF filter toàn cục.

## 7. Frontend React

### 7.1 API layer

Tạo `frontend/src/features/reviews/reviews.api.ts`:

- Runtime parsers tại trust boundary.
- `getPublicReviews`, `getReviewEligibility`, `createReview`, `updateReview`, `deleteReview`.
- `getModerationReviews`, `moderateReview`.
- CSRF token cho mutation.
- Giữ error code server để UI hiển thị đúng `REVIEW_NOT_ELIGIBLE`, `REVIEW_ALREADY_EXISTS` và conflict.

### 7.2 Reusable components

- `RatingStars`: radio group keyboard-accessible, label rõ ràng, không chỉ dùng màu.
- `RatingSummary`: average, count, progress cho 1–5 sao.
- `ReviewCard`: verified-purchase badge, masked name, thời gian, trạng thái owner.
- `ReviewForm`: create/edit, counter 2.000 ký tự, validation inline, loading/disabled state.
- `ReviewList`: sort và pagination.
- `ReviewModerationPanel`: filter, hide/restore, reason và confirm dialog.

### 7.3 Product detail và catalog

- `productLoader` tải Product + public reviews bằng `Promise.all`.
- Nếu session là Customer, tải thêm eligibility/current review; Guest không gọi protected endpoint.
- Trang Product Detail thêm tab/khu vực `#reviews`, summary, review list và form phù hợp trạng thái.
- `ProductCard` hiển thị sao trung bình + số lượt; `0 review` hiển thị “Chưa có đánh giá”, không giả `5.0`.
- Sau create/update/delete dùng React Router revalidation để summary và list đồng bộ từ server.

### 7.4 Customer Orders

- Thêm `productId` vào `CustomerOrderItemDto` và frontend order item parser.
- Chỉ hiện CTA “Đánh giá sản phẩm” khi Order `COMPLETED` và Shipment `DELIVERED`.
- CTA dẫn đến `/products/{productId}#reviews`; backend vẫn là authority cho eligibility.

### 7.5 Moderation workspace

- Thêm tab Review vào Staff Dashboard và Admin Dashboard, dùng chung `ReviewModerationPanel`.
- Hidden review hiển thị reason, moderator và thời điểm.
- Không cho UI sửa nội dung/rating của Customer.

## 8. Test matrix bắt buộc

### 8.1 Backend integration tests — `ProductReviewTests.cs`

1. Public đọc review/summary không cần đăng nhập; hidden/deleted không xuất hiện.
2. Guest/Staff/Admin không thể gọi Customer mutations; Customer không thể gọi moderation.
3. Customer chưa mua, đơn pending/cancelled hoặc shipment chưa DELIVERED nhận `REVIEW_NOT_ELIGIBLE`.
4. Customer có nhiều SKU cùng Product vẫn chỉ tạo một review Product.
5. Create hợp lệ trả 201 và lưu đúng qualifying order item.
6. Validation rating/content trả 400.
7. Owner update/delete thành công; cross-owner trả 404.
8. Hidden review sau Customer update vẫn hidden.
9. Staff và Admin hide/restore thành công, audit log đúng before/after; Guest/Customer bị chặn.
10. Aggregate average/count/distribution chỉ tính public active reviews và cập nhật sau edit/delete/moderation.
11. Hai create đồng thời cùng Customer/Product: đúng một 201, một 409; database chỉ có một active review, không 500.
12. Customer update/delete cạnh tranh moderator: kết quả tuyến tính, deleted/hidden invariant được giữ.
13. Pagination/sort deterministic và extreme page không overflow.
14. Catalog list/detail trả đúng average/count và không lộ review của Product không public.

### 8.2 Frontend tests

1. Product detail render summary, distribution, list và empty state.
2. Eligible Customer tạo/sửa/xóa review với CSRF và revalidation.
3. Guest hoặc Customer không eligible không thấy form sai quyền.
4. ProductCard hiển thị rating đúng; malformed API payload bị parser từ chối an toàn.
5. Staff/Admin moderation panel hide/restore và hiển thị server error.
6. Orders CTA chỉ xuất hiện cho đơn COMPLETED + DELIVERED.
7. Accessibility: rating radio group có label, keyboard interaction và error announcement.

### 8.3 Playwright E2E

Mở rộng commerce journey hiện có sau bước giao thành công:

1. Customer mở đúng đơn đã DELIVERED.
2. Click CTA đánh giá đúng Product.
3. Chọn 5 sao, nhập nhận xét và submit.
4. Review xuất hiện với badge “Đã mua hàng”.
5. Trang Product/Card cập nhật average/count.
6. Staff ẩn review; public không còn thấy và aggregate giảm.
7. Admin khôi phục; public thấy lại và aggregate phục hồi.

Không dùng `if (isVisible())`, không mock backend, không phụ thuộc tài khoản dùng chung có lịch sử cũ.

## 9. Trình tự triển khai

| Bước | Công việc | Điều kiện hoàn tất |
|---|---|---|
| T36.1 | Migration 008 + EF model/mapping + fixture | Fresh DB và integration fixture áp schema thành công |
| T36.2 | Contracts + service create/owner operations | Eligibility/ownership/unique invariant có tests |
| T36.3 | Moderation + audit + concurrency guards | Staff/Admin transitions và race tests pass |
| T36.4 | Public review query + catalog aggregate | Aggregate và pagination tests pass |
| T36.5 | API controllers/ProblemDetails/policy | Contract/RBAC tests pass |
| T36.6 | Product/Card/Orders UI | Customer review flow và frontend tests pass |
| T36.7 | Staff/Admin moderation UI | Shared panel và tests pass |
| T36.8 | Playwright + docs + release gate | Live E2E, full regression và Graphify pass |

Mỗi bước phải giữ build xanh; không gom toàn bộ thay đổi rồi mới kiểm thử.

## 10. Definition of Done

T36 chỉ được đánh dấu hoàn tất khi:

- Migration chạy được trên DB trống và có hướng dẫn áp dụng lên DB hiện hữu.
- Không có review từ đơn chưa giao; owner isolation và RBAC pass.
- Unique active review được bảo vệ bởi PostgreSQL, không chỉ kiểm tra ứng dụng.
- Aggregate chính xác sau create/update/delete/hide/restore và dưới concurrency.
- Không lộ PII trong public contract và không stored XSS.
- Moderation có audit cùng transaction.
- Backend integration tests toàn bộ pass.
- Frontend tests, lint và production build pass.
- Playwright live Docker stack pass, không mock backend/SMTP.
- `docs/api/reviews.md`, `docs/TASKS.md`, `README.md`, release checklist được cập nhật.
- Chạy `graphify update .` sau mọi thay đổi code cuối cùng.

## 11. Ngoài phạm vi T36

- Review ảnh/video, reply của shop, helpful vote.
- Automated profanity/AI moderation.
- Review theo từng SKU thay vì Product.
- Reward/loyalty khi review.
- Import review bên thứ ba.
- Redis cache hoặc denormalized rating counter.
- Wishlist, voucher và PayOS.

