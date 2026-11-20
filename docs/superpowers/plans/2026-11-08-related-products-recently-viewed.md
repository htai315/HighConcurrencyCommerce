# T53 — Related Products & Recently Viewed

Ngày lập: 08/11/2026. Trạng thái: **Kế hoạch đã chốt — chưa triển khai code**.

Phụ thuộc đã hoàn tất: T36 Reviews, T44 Brand & Product Specifications, T46 Product Lifecycle, T50 Advanced Search, T51 Wishlist Product Alerts và T52 Product Comparison. PayOS T19–T22 tiếp tục deferred theo ADR 0003.

## 1. Mục tiêu

T53 bổ sung hai công cụ khám phá sản phẩm không yêu cầu đăng nhập:

1. Trên PDP, hiển thị tối đa sáu sản phẩm tương tự còn public, cùng danh mục và được xếp thứ tự xác định theo khả dụng, thương hiệu, khoảng giá và đánh giá.
2. Trên Storefront/PDP, hiển thị lịch sử tối đa 12 sản phẩm đã xem gần đây bằng dữ liệu Catalog mới nhất, có TTL và nút xóa rõ ràng.
3. Không theo dõi hành vi ở server, không dùng AI/ML, không tạo hồ sơ người dùng và không tuyên bố một sản phẩm là “tốt nhất”.

Kết quả phải dùng lại `ProductSummaryDto`/`ProductCard`, bảo toàn public visibility của Catalog, không N+1 và không làm hỏng PDP nếu khối gợi ý phụ bị lỗi.

## 2. Phạm vi

### Trong T53

- Anonymous API lấy Product tương tự theo một Product nguồn public.
- Anonymous batch summary API hydrate danh sách recent IDs theo thứ tự client.
- Related V1 chỉ lấy Product có cùng underlying `category_id`; không fallback khác danh mục.
- Ranking deterministic, không random và không thay đổi do thứ tự database.
- Recent history lưu `productId + viewedAt` trong localStorage versioned, tối đa 12 mục, TTL 30 ngày.
- Dữ liệu giá/tồn/ảnh/title hiển thị luôn hydrate lại từ API; localStorage không phải nguồn giá/tồn.
- Khối related trên PDP; khối recent trên Storefront và PDP, loại Product hiện tại khỏi khối PDP.
- Reuse ProductCard để Wishlist/Compare hoạt động nhất quán.
- Responsive CSS grid/horizontal scroll, không auto-carousel.
- Runtime parser, corruption/private-mode recovery, stale-request guard và accessibility đầy đủ.
- Backend integration, frontend unit/component, performance guard và Playwright Flow 19.

### Ngoài T53

- Không lưu recent history vào PostgreSQL hoặc đồng bộ nhiều thiết bị/tài khoản.
- Không analytics, click tracking, impression tracking, fingerprint hoặc external recommendation provider.
- Không collaborative filtering, vector embedding, AI/ML hoặc personalization theo Customer/order history.
- Không tự gợi ý phụ kiện khác danh mục; cần compatibility/tagging riêng ở task tương lai.
- Không sponsored placement, merchandising pin hoặc Admin cấu hình recommendation.
- Không dùng Flash Sale price trong ranking; ProductCard vẫn có thể hiển thị contract public hiện hành nhưng ranking chỉ dựa regular price.
- Không migration/database table mới.

## 3. Quyết định nghiệp vụ

1. Product nguồn phải đi qua cùng `PublicProducts` predicate: `PUBLISHED`, có ít nhất một SKU `ACTIVE`, không suspended/archived/draft. Nếu không còn public, related endpoint trả 404 generic.
2. Candidate phải khác Product nguồn, cùng non-null underlying `category_id`, và cũng đi qua `PublicProducts`.
3. Category/brand ẩn không được lộ metadata. Underlying category vẫn dùng nội bộ để lọc chính xác như T52.
4. Related mặc định 6, cho phép `limit=1..12`; không phân trang ở V1.
5. Ranking dùng tuple ổn định theo thứ tự:
   - Candidate có ít nhất một SKU `ACTIVE` với `available_qty > 0` trước.
   - Cùng active brand với Product nguồn trước; brand null/ẩn không tạo lợi thế.
   - Khoảng cách tuyệt đối giữa `min(regular_price)` của candidate và nguồn tăng dần.
   - `averageRating` published giảm dần.
   - `reviewCount` published giảm dần.
   - `Product.Id` tăng dần làm tie-break cuối.
6. Không trả score/rank reason nội bộ ra API. UI dùng câu trung tính “Sản phẩm tương tự”, không dùng “Tốt nhất cho bạn”.
7. Recent history chỉ được ghi sau khi PDP loader đã trả một Product public thành công. 404/error không ghi lịch sử.
8. Xem lại một Product đưa nó lên đầu và cập nhật `viewedAt`; không duplicate.
9. TTL là 30 ngày tính theo UTC epoch milliseconds. Parser loại item hết hạn, ID sai/Empty, timestamp tương lai quá 5 phút hoặc dữ liệu vượt giới hạn.
10. Giới hạn cứng 12 recent IDs. Local state vẫn hoạt động khi storage bị chặn; persistence là best-effort.
11. Batch hydrate giữ đúng thứ tự ID request, bỏ Product không còn public và trả `omittedCount` generic; không tiết lộ lifecycle.
12. Related/recent là nội dung phụ. Lỗi 429/5xx/malformed response chỉ ẩn section hoặc hiện retry cục bộ; PDP/Storefront chính vẫn dùng được.

## 4. Public API contract

Base URL: `/api/v1/catalog`. Cả hai endpoint anonymous, read-only, không CSRF và `Cache-Control: no-store` vì price/availability thay đổi.

### 4.1 GET `/products/{id}/related?limit=6`

Validation:

- `id` là Guid khác Empty.
- `limit` từ 1 đến 12, mặc định 6.

Response:

```json
{
  "sourceProductId": "...",
  "items": [
    {
      "id": "...",
      "title": "Vợt cầu lông ...",
      "category": { "id": "...", "name": "Vợt cầu lông", "slug": "vot-cau-long" },
      "brand": { "id": "...", "name": "Yonex", "slug": "yonex", "country": "Nhật Bản", "websiteUrl": null },
      "minPrice": 3850000,
      "inStock": true,
      "imageUrl": "...",
      "averageRating": 4.8,
      "reviewCount": 20
    }
  ]
}
```

Errors:

- `400 VALIDATION_ERROR`: Guid Empty hoặc limit ngoài biên.
- `404 PRODUCT_NOT_FOUND`: Product nguồn không tồn tại hoặc không public; không phân biệt nguyên nhân.
- `429`: public Catalog rate limit.
- `500 SERVER_ERROR`: lỗi chung, không lộ SQL/ranking internals.

### 4.2 GET `/products/batch?id={id}&id={id}`

Validation:

- Bắt buộc 1–12 Guid duy nhất, khác Empty.
- Duplicate, malformed hoặc quá 12 trả `400 VALIDATION_ERROR`.

Response:

```json
{
  "items": [],
  "requestedCount": 3,
  "omittedCount": 1
}
```

- `items` là `ProductSummaryDto`, giữ đúng request order.
- Product không còn public bị omit generic.
- Không trả trạng thái Draft/Suspended/Archived hoặc nguyên nhân bị omit.

## 5. Backend design

### 5.1 Application contracts

Mở rộng `CatalogContracts.cs`:

- `RelatedProductsQuery` với `[Range(1, 12)] Limit = 6`.
- `RelatedProductsDto(Guid SourceProductId, IReadOnlyList<ProductSummaryDto> Items)`.
- `ProductBatchQuery : IValidatableObject`, cap 12, unique/non-empty.
- `ProductSummaryBatchDto(Items, RequestedCount, OmittedCount)`.
- `ICatalogReader.GetRelatedProductsAsync(Guid sourceId, int limit, CancellationToken)`.
- `ICatalogReader.GetProductSummariesAsync(IReadOnlyList<Guid> ids, CancellationToken)`.

Không thêm domain entity hoặc recommendation score contract.

### 5.2 Infrastructure

`PostgresCatalogReader` dùng lại `PublicProducts` và public summary projection:

- Trích expression/helper projection nếu EF Core dịch thành SQL ổn định; nếu không, giữ projection tường minh cùng contract tests chống drift.
- Related tối đa hai DB commands cố định: một command lấy source metadata (`categoryId`, active brand, min active-SKU price), một command lấy candidates. Không command theo từng Product.
- Batch summary đúng một DB command, materialize tối đa 12 và reorder trong memory theo request.
- Related candidate query không `Include`, không lazy loading và không client-side materialize trước `OrderBy/Take`.
- Review aggregate chỉ tính `PUBLISHED` và `deleted_at IS NULL`.
- Availability chỉ dựa `available_qty > 0`; không trả physical/reserved/allocated.
- Command timeout 2 giây, lưu/restore timeout cũ trong `finally`, cancellation truyền tới EF.
- Không transaction/row lock cho public read. Snapshot tại thời điểm query là đủ.

### 5.3 Controller

Mở rộng `CatalogController`:

- `GET products/{id:guid}/related` đặt cạnh detail route.
- `GET products/batch` không xung đột `{id:guid}`.
- `ResponseCache(NoStore = true, Location = None)` và header convention hiện có.
- 404 source dùng cùng error envelope Catalog, có `correlationId`.
- Không log ID/title bị omit ở warning level cho public stale history bình thường.

### 5.4 Performance gate

- Integration interceptor xác nhận related `<= 2` DB commands và batch `= 1` command.
- Mọi command dùng timeout 2 giây và timeout được restore.
- EXPLAIN benchmark trên fixture tối thiểu 10.000 Product/30.000 SKU hiện có:
  - Không truy vấn theo từng candidate.
  - Candidate lọc category/public trước limit.
  - p95 target cục bộ không được dùng làm SLA production; gate chính là plan/index shape và bounded commands.
- Chỉ thêm index/migration nếu EXPLAIN chứng minh index hiện tại không đáp ứng. Mặc định T53 không migration.

## 6. Frontend state và data flow

### 6.1 Storage contract

Feature mới `frontend/src/features/recent-products/`:

- Key `hcc.recent-products.v1`.
- Stored item: `{ productId: string, viewedAt: number }`.
- `MAX_RECENT_PRODUCTS = 12`, `RECENT_TTL_MS = 30 days`.
- Parser kiểm tra GUID theo contract .NET/PostgreSQL, loại `Guid.Empty`, dedupe giữ item mới nhất, sort `viewedAt desc`, prune TTL và cap 12.
- JSON hỏng bị xóa an toàn; storage unavailable không làm crash.
- `storage` event + custom event đồng bộ tab/cùng tab.
- API: `recordView(productId)`, `remove(productId)`, `clear()`, `ids`.

Không lưu title, image, price, category hoặc email/user ID trong localStorage.

### 6.2 API/parser

Mở rộng `catalog.api.ts` hoặc tách `discovery.api.ts`:

- `getRelatedProducts(productId, signal, limit=6)`.
- `getProductSummaries(ids, signal)`.
- Strict parser tái sử dụng `ProductSummary`; xác nhận source ID, count/order/unique/subset và omitted arithmetic.
- Image URL tiếp tục qua normalization an toàn hiện có.
- Abort request khi Product route đổi/unmount; response cũ không được ghi đè section của Product mới.

### 6.3 Product Detail

- Chỉ `recordView(product.id)` trong effect sau khi loader đã thành công.
- `RelatedProductsSection` fetch theo `product.id`; loading skeleton có `aria-busy`, error cục bộ có nút retry.
- `RecentlyViewedSection` hydrate IDs, loại `product.id` hiện tại trước render.
- Related và recent không được chặn ảnh, review, cart, Wishlist hoặc Comparison của PDP chính.

### 6.4 Storefront

- `RecentlyViewedSection` đặt sau hero/flash-sale và trước hoặc sau Catalog theo đánh giá layout thực tế.
- Chỉ render khi API còn ít nhất một Product public.
- Có nút “Xóa lịch sử đã xem”; sau clear focus chuyển về heading/empty status hợp lý.
- Không tự fetch khi storage rỗng.

## 7. UI/UX và accessibility

- Dùng `ProductCard` để nhất quán price/rating/Wishlist/Compare.
- Section có heading rõ: “Sản phẩm tương tự” và “Bạn đã xem gần đây”.
- Copy trung tính: không nói “dành riêng cho bạn” vì không personalization server-side.
- Desktop grid 3–4 cột; tablet 2–3; mobile horizontal scroll có snap hoặc một cột, không overflow body.
- Không auto-play, không timer, không carousel vô hạn.
- Nếu có nút cuộn, nút có accessible name, disabled đúng biên và keyboard không bị giữ focus ngoài viewport.
- Loading không announce lặp; lỗi section dùng `role=status`, lỗi PDP chính vẫn theo route error hiện tại.
- `prefers-reduced-motion` tắt smooth scroll/transition không thiết yếu.
- Clear history cần một thao tác nhưng không modal xác nhận vì chỉ xóa dữ liệu cục bộ, có live confirmation và có thể bắt đầu ghi lại khi xem PDP tiếp theo.

## 8. Security và privacy

- Server chỉ nhận Product IDs cần hydrate/related; không nhận recent timestamp, session identifier hoặc toàn bộ browsing history ngoài tối đa 12 ID trong request hiện tại.
- Không gắn API với Customer ID kể cả khi đang đăng nhập.
- Không log danh sách recent IDs ở Information/Warning; access log hạ tầng áp dụng chính sách chung.
- Không render HTML từ title/metadata; React text escaping mặc định.
- Query cap/unique/non-empty trước DB, encode bằng `URLSearchParams`.
- Không tin localStorage hoặc API; parser runtime ở cả hai boundary.
- CSP/image policy giữ nguyên; URL ảnh nguy hiểm bị loại.
- Product unavailable bị omit generic để không lộ lifecycle.

## 9. Test matrix bắt buộc

### Backend integration

1. Anonymous lấy related thành công; không yêu cầu session/CSRF.
2. Source missing/Draft/Suspended/Archived/no-active-SKU đều trả 404 generic.
3. Candidate chỉ cùng underlying category, public và khác source.
4. Category/brand ẩn không lộ metadata nhưng filtering/ranking vẫn đúng.
5. In-stock trước out-of-stock; same active brand, price distance, rating/review và ID tie-break đúng.
6. `limit` 1/6/12 hợp lệ; 0/13 trả 400.
7. Không lộ inventory partitions, lifecycle, creator, revision hoặc internal rank score.
8. Related bounded tối đa hai commands, timeout 2 giây và restore.
9. Batch giữ order, omit unavailable và arithmetic count đúng.
10. Batch zero/duplicate/Empty/malformed/13 IDs trả 400, không 500.
11. Batch đúng một DB command và không public-field leak.
12. Lifecycle đổi giữa các request không làm cache stale; response `no-store`.

### Frontend unit/component

13. Storage parser xử lý corrupt JSON, GUID demo, Empty, duplicate, TTL, future timestamp và cap 12.
14. Record view idempotent, move-to-front và cross-tab/custom event.
15. Storage unavailable vẫn giữ memory state; clear hoạt động và focus hợp lý.
16. Batch/related parser từ chối malformed count/order/duplicate/out-of-request/rating/image.
17. PDP thành công mới record; 404/error không record.
18. Product route đổi hủy request/stale response; related error không làm hỏng PDP.
19. Recent hydrate theo storage order, omit unavailable, loại current Product trên PDP.
20. Related/recent cards giữ Wishlist/Compare controls và semantic section.
21. Empty/loading/error/retry/clear states và mobile overflow đạt.

### Playwright Flow 19

Trên Docker demo stack thật:

1. Guest mở vợt A rồi vợt B; recent history hiển thị B trước A sau reload.
2. Storefront hydrate title/price/tồn mới từ API, không dùng snapshot local.
3. PDP vợt A hiển thị related vợt B nhưng không hiển thị giày khác danh mục.
4. Related card mở đúng PDP; Wishlist/Compare control vẫn hoạt động.
5. Context trình duyệt mới không có recent history, chứng minh không lưu server/cookie account.
6. Xóa recent history làm section biến mất và giữ focus/accessibility.
7. Admin suspend candidate; reload related/recent omit generic, không lộ `SUSPENDED`; resume trong `finally`.

Regression bắt buộc: Flow 5 Wishlist, Flow 12 lifecycle, Flow 16 search, Flow 17 alerts và Flow 18 comparison.

## 10. Chia task triển khai

| Task | Công việc | Điều kiện hoàn tất |
|---|---|---|
| T53.1 | Chốt contract + API docs skeleton + application DTO/query | Contract tests cap/validation/ranking order; không migration |
| T53.2 | Related reader/API + public projection/ranking | Visibility/category/ranking/404 tests xanh; `<= 2` DB commands |
| T53.3 | Batch summary API | Order/omit/validation/public projection; đúng 1 DB command |
| T53.4 | Recent storage/context/parser | TTL/dedupe/cap/private-mode/cross-tab tests xanh |
| T53.5 | Related + Recent UI trên PDP/Storefront | Reuse ProductCard, abort/stale guard, loading/error/retry/clear/a11y |
| T53.6 | Performance/security/privacy hardening | Timeout/restore, query-plan gate, no field leak/logging/history sync |
| T53.7 | Full regression + responsive/accessibility | Backend/frontend/lint/build/base+demo Compose sạch |
| T53.8 | Playwright Flow 19 + CI/docs/Graphify | Flow 19 + regressions Docker thật; CI discovery; roadmap/release docs đồng bộ |

Thứ tự: T53.1 → T53.2 → T53.3 → T53.4 → T53.5 → T53.6 → T53.7 → T53.8.

## 11. Files dự kiến

- `HighConcurrencyCommerce.Application/Catalog/CatalogContracts.cs`
- `HighConcurrencyCommerce.Infrastructure/Catalog/PostgresCatalogReader.cs`
- `HighConcurrencyCommerce.Api/Controllers/CatalogController.cs`
- `HighConcurrencyCommerce.IntegrationTests/RelatedProductsTests.cs`
- `frontend/src/features/recent-products/recent-products-storage.ts`
- `frontend/src/features/recent-products/RecentProductsContext.tsx`
- `frontend/src/features/recent-products/RecentlyViewedSection.tsx`
- `frontend/src/features/catalog/RelatedProductsSection.tsx`
- `frontend/src/features/catalog/catalog.api.ts` hoặc `discovery.api.ts`
- `frontend/src/pages/ProductDetailPage.tsx`
- `frontend/src/pages/StorefrontPage.tsx`
- CSS responsive feature/page
- `frontend/src/test/product-discovery.test.tsx`
- `frontend/e2e/19-related-recent-products.spec.ts`
- `docs/api/product-discovery.md`
- `.github/workflows/ci.yml`
- `docs/operations/e2e-testing-guide.md`, release checklist, `README.md`, `docs/TASKS.md`

## 12. Definition of Done

T53 chỉ đóng khi:

- Related không trả Product khác category/non-public/current và ranking deterministic đúng contract.
- Batch hydrate giữ order, omit generic, cap 12 và không N+1.
- Related dùng tối đa hai DB commands; batch đúng một; timeout 2 giây có restore.
- Local history TTL/dedupe/cap/corrupt/private-mode hoạt động và không chứa giá/tồn/PII.
- PDP/Storefront chính không fail khi related/recent API lỗi.
- Không stale response khi chuyển nhanh giữa PDP.
- Không có server-side tracking, analytics hoặc account sync.
- Responsive 320px, keyboard, focus, reduced-motion và screen-reader states đạt.
- Full backend/frontend/lint/build/base+demo Compose pass.
- Flow 19 và regressions 5/12/16/17/18 pass trên Docker thật.
- CI discover Flow 19; Graphify cập nhật sau source code cuối.

## 13. Rủi ro và điểm dừng

- Nếu ranking query cần client-side sort hoặc materialize quá nhiều candidate, dừng và sửa SQL/index; không chấp nhận tải toàn category vào memory.
- Nếu EXPLAIN cho thấy sort price-distance không dùng được index nhưng category candidate quá lớn, cân nhắc shortlist có giới hạn theo price range trước; chỉ thêm migration sau bằng chứng query plan.
- Nếu Product không có category, related trả danh sách rỗng; không gom vào category giả.
- Nếu source không có active brand, bỏ tiêu chí same-brand thay vì coi mọi brand null là match.
- Nếu recent API lỗi, giữ ID local để retry lần sau; không xóa lịch sử chỉ vì lỗi mạng. Chỉ prune IDs được server xác nhận omit trong response hợp lệ.
- Nếu ProductCard quá nặng trong horizontal rail, đo render trước khi tạo card rút gọn; không fork contract/UI khi chưa có bằng chứng.
- Cross-category complementary recommendations cần compatibility taxonomy/merchandising contract riêng, không mở rộng ngầm trong T53.

