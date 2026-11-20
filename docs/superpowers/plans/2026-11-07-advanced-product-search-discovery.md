# T50 — Advanced Product Search & Discovery

Ngày lập: 07/11/2026  
Trạng thái: **Kế hoạch đã chốt — chưa triển khai**

## 1. Mục tiêu

Nâng Catalog hiện tại từ tìm kiếm `ILIKE` theo riêng tiêu đề thành trải nghiệm khám phá sản phẩm đủ dùng cho shop cầu lông có dữ liệu lớn:

1. Tìm theo tên sản phẩm, mã SKU, phân loại SKU, thương hiệu và danh mục.
2. Tìm tiếng Việt không phân biệt dấu/hoa thường và hỗ trợ sai khác nhỏ bằng trigram.
3. Gợi ý nhanh khi nhập nhưng không tạo tải truy vấn không giới hạn.
4. Lọc kết hợp theo danh mục, thương hiệu, khoảng giá, còn hàng và mức đánh giá.
5. Sắp xếp theo độ liên quan, mới nhất, giá, đánh giá và bán chạy.
6. Giữ toàn bộ trạng thái trong URL để back/forward, reload và chia sẻ link hoạt động đúng.
7. Có UX desktop/mobile, keyboard navigation, screen-reader contract, loading/empty/error state rõ ràng.
8. Có bằng chứng query plan, giới hạn input và release gate trên PostgreSQL thật.

T50 tiếp tục dùng PostgreSQL và `ICatalogReader`; không đưa Elasticsearch/OpenSearch hoặc cache phân tán vào hệ thống khi chưa có KPI chứng minh cần thiết.

## 2. Hiện trạng và khoảng trống

Catalog hiện có:

- `GET /api/v1/catalog/products` với `q`, `categoryId`, `brandId`, `minPrice`, `maxPrice`, `sort`, `page`, `pageSize`.
- `q` chỉ dùng `ILIKE %text%` trên `products.title`.
- sort chỉ có `newest`, `price-asc`, `price-desc`.
- Storefront đã giữ bộ lọc trong query string và có responsive filter drawer.
- Product summary đã có `inStock`, `averageRating`, `reviewCount`.

Khoảng trống:

- Không tìm được SKU, option label, brand hoặc category.
- Từ khóa không dấu như `vot cau long` không khớp tốt với `vợt cầu lông`.
- `%keyword%` chưa có index phù hợp và sẽ chậm khi Catalog tăng.
- Không có autocomplete, lọc tồn kho/rating hoặc sort relevance/bestselling.
- Header search và Catalog filter chưa dùng chung một canonical query contract.
- Chưa có performance acceptance bằng `EXPLAIN (ANALYZE, BUFFERS)`.

## 3. Quyết định kiến trúc

### 3.1. PostgreSQL-native search

Migration `020_advanced_product_search.sql` bật hai extension:

- `unaccent`: chuẩn hóa tiếng Việt không dấu;
- `pg_trgm`: hỗ trợ index cho contains/prefix/fuzzy similarity.

Production preflight phải xác nhận database role có quyền `CREATE EXTENSION`. Nếu managed PostgreSQL không cho phép, deployment dừng với lỗi rõ ràng; không âm thầm quay về full table scan.

Không tạo search database riêng trong T50. Catalog write volume thấp, PostgreSQL đủ bảo đảm read-after-publish và giảm rủi ro eventual consistency.

### 3.2. Chuẩn hóa văn bản

Tạo hàm schema-owned, immutable:

```sql
store.catalog_search_normalize(value text)
```

Canonical normalization:

1. `coalesce(value, '')`;
2. `lower(...)`;
3. `unaccent(...)`;
4. thay chuỗi whitespace liên tiếp bằng một dấu cách;
5. trim đầu/cuối.

Thêm generated stored columns:

- `products.search_title_norm`;
- `product_skus.search_sku_norm`;
- `product_skus.search_option_norm`;
- `brands.search_name_norm`;
- `categories.search_name_norm`.

Generated columns tránh application quên cập nhật search projection khi Staff/Admin sửa dữ liệu hoặc approve Product Revision.

### 3.3. Index

Tối thiểu:

- partial GIN trigram trên `products.search_title_norm` cho product đang `PUBLISHED`;
- partial GIN trigram trên `product_skus.search_sku_norm` và `search_option_norm` cho SKU `ACTIVE`;
- GIN trigram trên brand/category normalized name;
- B-tree hỗ trợ `product_skus(product_id, sku_status, regular_price)`;
- B-tree/partial index phục vụ kiểm tra tồn khả dụng;
- index đọc lịch sử bán hàng chỉ dùng khi sort `best-selling`.

Migration phải idempotent, chạy được trên database sạch, database legacy và reapply qua migration runner.

### 3.4. Không có synchronous popularity counter

`best-selling` là read model xếp hạng, không phải business invariant. T50 V1 tính từ source-of-truth:

- Order `COMPLETED`;
- Shipment `DELIVERED`;
- tổng quantity theo Product trong cửa sổ 90 ngày;
- tie-break `Product.Id` để phân trang xác định.

Chỉ join/aggregate nhánh này khi caller chọn `sort=best-selling`. Không tăng counter trong fulfillment transaction vì sẽ thêm Product lock vào đường ghi tiền/tồn và làm xấu lock order.

Nếu query-plan gate không đạt với dữ liệu benchmark, T50 dừng để viết ADR projection bất đồng bộ; không thêm counter thiếu kiểm chứng.

### 3.5. Relevance ranking

Khi có `q`, score ưu tiên:

1. SKU exact;
2. SKU prefix;
3. title exact/prefix;
4. trigram similarity của title;
5. option label;
6. brand;
7. category.

Sort mặc định khi có `q` là `relevance`; khi không có `q` là `newest`. Tie-break cuối luôn là `Product.Id`.

Không trả HTML highlight từ backend. Frontend tự chia text node để tô phần khớp, tránh XSS và coupling presentation vào API.

## 4. Contract API

### 4.1. Product search

Giữ endpoint:

```http
GET /api/v1/catalog/products
```

Query contract sau T50:

| Field | Quy tắc |
|---|---|
| `q` | nullable, trim, tối đa 100 ký tự; chuỗi rỗng coi như null |
| `categoryId` | UUID nullable |
| `brandId` | UUID nullable |
| `minPrice` / `maxPrice` | VND không âm; min <= max |
| `inStock` | nullable boolean; `true` chỉ lấy Product có SKU active với available > 0 |
| `minRating` | nullable integer 1–5 |
| `sort` | `relevance`, `newest`, `price-asc`, `price-desc`, `rating-desc`, `best-selling` |
| `page` | 1–100.000 |
| `pageSize` | 1–48, mặc định 12 |

Rules:

- `sort=relevance` không có `q` trả `400 VALIDATION_ERROR` hoặc canonicalize thành `newest`; chốt lựa chọn ưu tiên là trả 400 để contract rõ ràng.
- `q` một ký tự vẫn được search khi submit thủ công nhưng autocomplete chỉ chạy từ hai ký tự.
- Giá lọc trên SKU `ACTIVE`; Product match khi có ít nhất một SKU cùng thỏa khoảng giá.
- `inStock=true` kiểm tra `inventory_balances.available_qty > 0`, không dùng physical quantity.
- Rating chỉ tính review `PUBLISHED` và chưa soft-delete.
- Product `DRAFT|SUSPENDED|ARCHIVED`, category hidden, brand inactive không được xuất hiện hoặc góp suggestion.

Response mở rộng tương thích:

```json
{
  "items": [],
  "totalCount": 0,
  "page": 1,
  "pageSize": 12,
  "query": {
    "normalizedText": "vot yonex",
    "effectiveSort": "relevance"
  },
  "facets": {
    "categories": [{ "id": "...", "name": "Vợt cầu lông", "count": 12 }],
    "brands": [{ "id": "...", "name": "Yonex", "count": 8 }],
    "inStockCount": 15,
    "priceMin": 250000,
    "priceMax": 4290000
  }
}
```

Facet count áp dụng toàn bộ điều kiện hiện tại ngoại trừ chính dimension đang đếm:

- category facets bỏ `categoryId` nhưng giữ q/brand/price/stock/rating;
- brand facets bỏ `brandId` nhưng giữ các điều kiện khác;
- price range và stock count giữ q/category/brand/rating.

Chỉ trả tối đa 50 category và 50 brand facet, sort count giảm dần rồi name/id xác định.

### 4.2. Autocomplete

Endpoint mới:

```http
GET /api/v1/catalog/search/suggestions?q={text}&limit={1..10}
```

- Public `[AllowAnonymous]`.
- `q`: 2–80 ký tự sau trim; ngắn hơn trả mảng rỗng, không truy vấn fuzzy.
- `limit`: mặc định 8, tối đa 10.
- Rate limit riêng theo IP/session, không làm ảnh hưởng catalog browsing.
- `Cache-Control: public, max-age=30`; không chứa dữ liệu cá nhân.
- CancellationToken được truyền xuyên xuống Npgsql.

DTO:

```json
[
  {
    "kind": "PRODUCT",
    "label": "Vợt cầu lông Yonex Astrox 88D Pro",
    "secondaryText": "Yonex · Vợt cầu lông",
    "productId": "...",
    "queryValue": "Vợt cầu lông Yonex Astrox 88D Pro"
  },
  {
    "kind": "SKU",
    "label": "AX88D-PRO-4U",
    "secondaryText": "4U / G5 (Navy/Gold)",
    "productId": "...",
    "queryValue": "AX88D-PRO-4U"
  }
]
```

Allow-list `kind`: `PRODUCT|SKU|BRAND|CATEGORY`. Exact/prefix trước fuzzy; dedupe theo `(kind, label, productId)`.

## 5. Backend design

### 5.1. Application contracts

Mở rộng `CatalogContracts.cs`:

- `CatalogQuery` với `InStock`, `MinRating`, sort allow-list mới;
- `CatalogQueryMetadataDto`;
- `CatalogFacetDto`, `CatalogFacetsDto`;
- `SearchSuggestionQuery`, `SearchSuggestionDto`;
- `ICatalogReader.GetSuggestionsAsync(...)`.

Không nhận raw SQL sort column từ client. Sort luôn qua switch allow-list.

### 5.2. Query implementation

Tách `PostgresCatalogReader` thành các thành phần nhỏ nếu file vượt trách nhiệm:

- `CatalogSearchNormalizer` cho canonical query input;
- `PostgresCatalogSearchReader` cho product search/facets/suggestions;
- giữ detail/categories/brands trong reader hiện tại hoặc facade `ICatalogReader`.

Query product cần:

- bắt đầu từ `PublicProducts` dùng chung lifecycle guard;
- search bằng normalized generated columns;
- dùng `EXISTS` cho SKU để không nhân dòng Product;
- aggregate rating/sales một lần bằng subquery thay vì lặp correlated expression không cần thiết;
- count, facet và item đều dùng cùng predicate builder để tránh contract lệch;
- offset tính bằng `long`, không overflow;
- mọi ordering có deterministic tie-break.

Nếu EF Core không biểu đạt rank/index-friendly SQL rõ ràng, cho phép một query SQL parameterized riêng trong Infrastructure. Không nối chuỗi từ input và phải có integration tests chống wildcard/quote injection.

### 5.3. Controller và error contract

Mở rộng `CatalogController`:

- endpoint suggestions;
- `Cache-Control` phù hợp public read;
- model validation dùng ProblemDetails hiện có;
- rate limiter policy `catalog-suggestions`.

Mã lỗi:

| HTTP | Code | Ý nghĩa |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Query/filter/sort không hợp lệ |
| 429 | — | Vượt giới hạn autocomplete |
| 500 | — | Chỉ dành cho lỗi ngoài dự kiến; không được lọt lỗi PostgreSQL do input |

Không phản hồi chi tiết query plan, SQL hoặc extension error cho public client.

## 6. Frontend UX

### 6.1. Search box dùng chung

Tạo reusable `CatalogSearchBox` dùng tại `SiteHeader` và Storefront:

- controlled input đồng bộ với URL;
- debounce 250 ms cho suggestion;
- abort request cũ khi text đổi;
- không request khi < 2 ký tự;
- dropdown tối đa 8 item;
- keyboard: ArrowUp/ArrowDown, Enter, Escape;
- combobox/listbox/option ARIA đúng chuẩn;
- click ngoài đóng dropdown;
- loading skeleton nhỏ, empty suggestion và retry state;
- Enter không chọn suggestion sẽ submit `q` và reset `page=1`.

Header và filter form dùng cùng canonical URL helper, tránh hai search box tạo query khác nhau.

### 6.2. Bộ lọc

Storefront bổ sung:

- `Chỉ còn hàng`;
- `Đánh giá từ 4★`, `3★`, ...;
- brand/category kèm count;
- price min/max theo facet range;
- sort relevance/rating/best-selling;
- active chips cho mọi filter;
- nút xóa từng filter và xóa tất cả;
- mọi thay đổi filter reset page về 1;
- thay sort giữ page nếu trang còn hợp lệ, nhưng ưu tiên reset page 1 để tránh empty page khó hiểu.

Desktop dùng sidebar; mobile dùng drawer hiện tại với sticky `Áp dụng`/`Xóa`. Khi drawer mở phải quản lý focus và Escape; không khóa body vĩnh viễn sau unmount.

### 6.3. Search history cục bộ

- Lưu tối đa 5 từ khóa đã submit thành công trong `localStorage`.
- Không lưu chuỗi rỗng, dedupe case/diacritic-normalized nhưng hiển thị text người dùng nhập gần nhất.
- Có nút xóa một mục/xóa toàn bộ.
- Không sync server, không coi là dữ liệu tài khoản và không đưa vào analytics trong T50.
- Account switch không làm rò history vì history là thiết bị/browser-level và UI phải ghi chú ngắn nếu hiển thị.

### 6.4. URL canonicalization

Thứ tự query key chuẩn:

```text
q, categoryId, brandId, minPrice, maxPrice, inStock, minRating, sort, page, pageSize
```

- Bỏ key rỗng/default khỏi URL khi có thể.
- Dùng `URLSearchParams`, không nối query thủ công.
- Giữ fragment `#catalog` khi thao tác filter/pagination từ Storefront.
- Back/forward phải phục hồi input, chips, sort, drawer result và pagination mà không state lệch URL.

## 7. Performance và giới hạn tải

### 7.1. Dataset benchmark

Tạo script/test data deterministic chỉ dùng cho test/performance:

- tối thiểu 10.000 Product;
- 30.000 SKU;
- 20 brand, 20 category;
- phân bố tồn kho, rating và order history đủ để các plan có tính đại diện.

Không đưa dataset lớn vào demo seed mặc định.

### 7.2. Query-plan gate

Thu `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` cho:

1. exact SKU;
2. title prefix không dấu;
3. fuzzy title;
4. q + brand + price + inStock + minRating;
5. suggestion;
6. best-selling 90 ngày;
7. page sâu trong giới hạn contract.

Acceptance trên CI/performance environment ổn định:

- search/suggestion dùng index phù hợp, không sequential scan toàn bảng search chính ở dataset chuẩn;
- p95 mục tiêu tham chiếu: suggestions < 150 ms, product search < 300 ms trên local PostgreSQL sau warm-up;
- không biến ngưỡng thời gian cứng thành flaky integration test thông thường; lưu plan assertions và benchmark report riêng;
- statement timeout cho public catalog search tối đa 2 giây.

### 7.3. Chống abuse

- giới hạn chiều dài query và page size;
- autocomplete rate limit;
- debounce + abort client;
- parameterized SQL;
- wildcard/metacharacter không thay đổi nghĩa query;
- không cho regex từ client;
- không cho sort expression tùy ý;
- request timeout/cancellation không để query tiếp tục vô ích.

## 8. Business invariants

1. Chỉ Product `PUBLISHED` với ít nhất một SKU `ACTIVE` xuất hiện.
2. Product `SUSPENDED|ARCHIVED|DRAFT` không xuất hiện trong result, facet hoặc suggestion.
3. Category hidden và brand inactive không xuất hiện/cộng count.
4. `inStock=true` chỉ dựa trên `available_qty > 0`.
5. Giá/rating/stock hiển thị vẫn lấy source-of-truth hiện tại, search index chỉ giúp tìm/rank.
6. Publish/suspend/resume/revision approve đọc thấy trạng thái mới ngay sau commit; không có search projection bất đồng bộ trong V1.
7. Exact SKU luôn xếp trước fuzzy title khi sort relevance.
8. Pagination ổn định dưới cùng snapshot query; tie-break ID bắt buộc.
9. Autocomplete không lộ Product chưa publish hoặc metadata vận hành nội bộ.
10. Search không ghi database và không tham gia lock order commerce.

## 9. Kiểm thử

### 9.1. Migration/integration backend

- Migration 020 fresh, legacy và reapply.
- Generated normalized columns cập nhật khi title/SKU/brand/category đổi.
- Tìm có dấu/không dấu, hoa/thường.
- Exact/prefix/fuzzy SKU/title/brand/category ranking.
- Wildcard `%`, `_`, backslash, quote và Unicode bất thường không gây injection/500.
- Lifecycle visibility: Draft/Suspended/Archived/hidden/inactive bị loại.
- Price, stock, rating filters riêng và kết hợp.
- Mọi sort cùng deterministic pagination.
- Deep page không overflow.
- Facet self-exclusion đúng.
- Suggestion validation, limit, dedupe và RBAC public.
- Cancellation/statement timeout không để connection lỗi.
- Best-selling chỉ đếm completed+delivered trong 90 ngày.
- Product publish/suspend/revision approve phản ánh ngay trong search.

### 9.2. Frontend tests

- Parser strict cho query/facets/suggestions.
- Debounce chỉ gửi request cuối.
- Abort response cũ không ghi đè suggestion mới.
- Keyboard combobox và ARIA states.
- Header submit đồng bộ Storefront URL.
- Filter chips, reset page, back/forward.
- Mobile drawer focus/Escape.
- inStock/rating/sort controls.
- loading/empty/error/retry.
- local history max 5, dedupe và clear.

### 9.3. Playwright Flow 16

1. Admin/Staff chuẩn bị một Product published có title tiếng Việt, brand, category và hai SKU.
2. Customer tìm bằng text không dấu và thấy đúng Product.
3. Autocomplete exact SKU điều hướng đúng PDP.
4. Quay lại Storefront, kết hợp brand + còn hàng + rating + price.
5. Đổi sort relevance/rating/best-selling và xác nhận URL canonical.
6. Reload/back/forward bảo toàn filter.
7. Mobile viewport mở drawer, áp dụng/xóa filter bằng keyboard.
8. Suspend Product rồi xác nhận result và suggestion không còn chứa Product.

Regression bắt buộc: Flow 4 (Reviews/rating), Flow 5 (Wishlist/catalog), Flow 11–13 (revision/lifecycle) và Flow 14 (Storefront checkout).

## 10. Tài liệu

- ADR 0015: PostgreSQL-native catalog search và ranh giới không dùng external search engine.
- `docs/api/catalog-search.md`.
- Cập nhật `docs/api/catalog.md` nếu đang tồn tại hoặc bổ sung link canonical.
- Cập nhật README, TASKS, E2E guide và release checklist.
- Lưu benchmark/query-plan report không chứa dữ liệu nhạy cảm.
- Graphify update sau code generation và trước handoff.

## 11. Chia task triển khai

| Bước | Hạng mục | Acceptance |
|---|---|---|
| T50.1 | ADR 0015 + migration 020 | **Hoàn tất 07/11/2026** — extensions, immutable normalize function, generated columns/index; fresh/legacy/reapply/concurrent pass |
| T50.2 | Contracts + query normalization | **Hoàn tất 07/11/2026** — filter/sort/facet/suggestion DTO, Vietnamese normalizer, validation và effective-sort metadata |
| T50.3 | Product search + relevance | **Hoàn tất 07/11/2026** — title/SKU/option/brand/category không dấu + fuzzy; lifecycle guard; deterministic paging |
| T50.4 | Facets + stock/rating + advanced sort | **Hoàn tất 07/11/2026** — facet self-exclusion, public rating, available stock, 90-day delivered best-selling |
| T50.5 | Suggestion API + rate limit | **Hoàn tất 07/11/2026** — exact/prefix/contains/fuzzy dedupe, cancellation, cache, rate limit, no unpublished leakage |
| T50.6 | Shared React search UX | **Hoàn tất 07/11/2026** — reusable Header/Storefront combobox, debounce 250 ms, abort + stale guard, keyboard/ARIA, retry/empty/loading, canonical URL và local history max 5 |
| T50.7 | Filter workspace responsive | **Hoàn tất 07/11/2026** — facet count/range, stock/rating/advanced sort, complete chips, canonical URL/back-forward và mobile drawer focus/Escape/scroll lock |
| T50.8 | Performance hardening | **Hoàn tất 07/11/2026** — disposable 10k/30k dataset, 7 EXPLAIN/25-sample cases, hard gate GIN/no search-table seq scan, command timeout 2 giây và benchmark report |
| T50.9 | Flow 16 + regression + docs/release gate | **Hoàn tất 07/11/2026** — Flow 16 và regression Flow 4, 5, 11–14 xanh trên Docker; CI chạy query-plan gate; full BE/FE/lint/build/Compose và tài liệu đồng bộ |

## 12. Release gate

Chỉ đóng T50 khi:

- Migration 020 chạy thành công trên database sạch, legacy và reapply.
- Full backend integration suite pass trên PostgreSQL thật.
- Full frontend tests, oxlint và production build pass.
- Compose base/demo config hợp lệ.
- Search không dấu, exact SKU, fuzzy title và combined filters có integration coverage.
- Không Product private/suspended/archived xuất hiện trong result/facet/suggestion.
- Query-plan gate chứng minh index được sử dụng trên dataset 10k/30k.
- Autocomplete rate limit, debounce, cancellation và stale-response guard pass.
- Playwright Flow 16 và regression Flow 4, 5, 11, 12, 13, 14 pass trên Docker demo stack.
- API docs, README, TASKS, ADR 0015 và Graphify được cập nhật.

## 13. Ngoài phạm vi T50

- Elasticsearch/OpenSearch/Algolia/Meilisearch.
- Cá nhân hóa dựa trên hành vi hoặc recommendation ML.
- Search analytics, tracking click/conversion hoặc hồ sơ quảng cáo.
- Synonym management UI.
- Voice/image search.
- Sponsored ranking/paid placement.
- Multi-language catalog ngoài tiếng Việt hiện tại.
- Cursor pagination; giữ page-based pagination để tương thích URL hiện hữu.
- Cache Redis/CDN invalidation phức tạp khi chưa có KPI.

## 14. Điểm dừng và rollback

- Nếu production PostgreSQL không cấp `unaccent/pg_trgm`, dừng deployment và cập nhật ADR; không fallback full scan.
- Nếu relevance SQL không dùng index ổn định, dừng T50.3 để điều chỉnh schema/query trước khi mở autocomplete.
- Nếu best-selling làm query vượt gate, tách projection async trong ADR bổ sung; không sửa fulfillment lock order.
- Migration rollback chỉ xóa generated columns/index/function sau khi application đã rollback về contract cũ.
- Frontend parser phải tương thích response cũ trong quá trình rolling deployment hoặc backend deploy trước frontend.

## 15. Thứ tự triển khai đề xuất

1. T50.1–T50.2: chốt ADR/schema/contract.
2. T50.3: search relevance và lifecycle correctness.
3. T50.4: facets, filters và sort.
4. T50.5: autocomplete sau khi query plan chính ổn định.
5. T50.6–T50.7: UX React và URL state.
6. T50.8: benchmark/hardening trước khi đóng UI contract.
7. T50.9: Flow 16, regression, docs, Graphify và release gate.
