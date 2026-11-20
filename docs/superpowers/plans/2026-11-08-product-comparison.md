# T52 — Product Comparison

Ngày lập: 08/11/2026. Trạng thái: **Hoàn tất — T52.1–T52.7**.

Phụ thuộc đã hoàn tất: T36 Reviews, T44 Brand & Product Specifications, T46 Product Lifecycle, T50 Advanced Search và T51 Wishlist Product Alerts. PayOS tiếp tục deferred theo ADR 0003; T52 không phụ thuộc đăng nhập hoặc dịch vụ ngoài.

## 1. Mục tiêu

Cho phép khách so sánh tối đa bốn sản phẩm cầu lông cùng danh mục trên một màn hình có URL chia sẻ được. Bảng so sánh phải dùng dữ liệu Catalog public hiện tại, làm rõ khác biệt về giá, tồn kho, thương hiệu, đánh giá, SKU và thông số kỹ thuật mà không tự suy diễn sản phẩm nào “tốt hơn”.

T52 ưu tiên ba kết quả:

1. Từ Catalog hoặc Product Detail, người dùng thêm/bỏ sản phẩm khỏi danh sách so sánh mà không cần đăng nhập.
2. `/compare?ids=<id1>,<id2>` tải dữ liệu public mới nhất trong một batch request và giữ đúng thứ tự URL.
3. Desktop, tablet và mobile đều đọc được bằng bảng semantic, cuộn ngang có kiểm soát và trạng thái bàn phím/screen reader rõ ràng.

## 2. Phạm vi

### Trong T52

- So sánh từ một đến bốn Product; CTA chính được khuyến nghị khi có ít nhất hai Product.
- Chỉ so sánh các Product có cùng `category_id` trong dữ liệu nguồn.
- Nút thêm/bỏ so sánh tại `ProductCard` và `ProductDetailPage`.
- Comparison tray dùng chung trên Storefront/PDP, cho phép xóa từng mục, xóa tất cả và mở trang so sánh.
- URL canonical có danh sách UUID đã deduplicate, giữ thứ tự lựa chọn và chia sẻ/mở tab mới được.
- `localStorage` account-agnostic để giữ lựa chọn trên cùng trình duyệt; URL là source of truth trên trang `/compare`.
- Batch Catalog API public, tối đa bốn ID, một read pipeline không N+1.
- Matrix gồm ảnh, tên, brand, giá thấp nhất, tồn kho, rating, danh sách SKU và union thông số theo `spec_key`.
- Generic notice khi Product trong URL không còn public/comparable; không lộ lifecycle nội bộ.
- Demo seed có thông số idempotent cho hai vợt cùng danh mục phục vụ E2E.
- Backend integration, frontend component/unit và Playwright Flow 18.

### Ngoài T52

- Không lưu comparison vào database hoặc đồng bộ nhiều thiết bị.
- Không recommendation, AI ranking, điểm số tổng hợp hoặc tuyên bố “sản phẩm tốt nhất”.
- Không so sánh Flash Sale price; chỉ dùng regular price hiện hành giống Catalog.
- Không thêm thẳng vào Cart từ matrix vì vẫn cần chọn SKU/quantity; CTA dẫn về PDP.
- Không so sánh khác danh mục, không tự map thông số có `spec_key` khác nhau.
- Không thêm migration mới; thay đổi demo seed không phải schema migration.
- Không tracking hành vi/analytics trong T52.

## 3. Quyết định nghiệp vụ

1. Giới hạn cứng là bốn UUID duy nhất. Frontend chặn mục thứ năm; backend vẫn validation độc lập.
2. Một Product có thể được chọn khi chưa đủ hai mục. Trang compare hiển thị gợi ý thêm sản phẩm thay vì lỗi.
3. Các Product còn hợp lệ phải có cùng `category_id`. Frontend chặn sớm khi có public category khác; backend là nguồn xác nhận cuối cùng, kể cả khi category đang ẩn khỏi projection public.
4. Product chỉ được trả về khi đi qua cùng public predicate của Catalog: `PUBLISHED`, có SKU `ACTIVE`, không archived/suspended/draft.
5. ID không tồn tại hoặc Product không còn public bị bỏ khỏi response và tính vào `omittedCount`; không phân biệt nguyên nhân để tránh lộ trạng thái nội bộ.
6. Nếu sau khi bỏ các mục unavailable, các Product còn lại khác danh mục, API trả `400 COMPARISON_CATEGORY_MISMATCH`.
7. Thứ tự cột đúng theo thứ tự ID trong request/URL, không theo title, giá hoặc thứ tự database.
8. Giá là `min(regular_price)` của SKU `ACTIVE`; tồn kho là có ít nhất một SKU `ACTIVE` có `available_qty > 0`.
9. SKU chỉ hiển thị `skuCode`, `optionLabel`, regular price và boolean còn hàng; không lộ physical/reserved/allocated quantity.
10. Spec matrix union theo `spec_key`; label lấy từ Product đầu tiên có key đó, thứ tự bằng `min(display_order)`, rồi `spec_key`. Thiếu giá trị hiển thị `—`.
11. Một dòng được đánh dấu “có khác biệt” khi các giá trị normalized (`trim`, so sánh ordinal-ignore-case) không đồng nhất; missing cũng là một giá trị khác. Đây chỉ là highlight trực quan, không phải xếp hạng.
12. Dữ liệu compare luôn lấy lại từ API khi mở/reload trang; snapshot trong `localStorage` chỉ chứa metadata tối thiểu để vẽ tray, không được dùng làm giá/tồn kho chính thức.

## 4. Public API contract

### Endpoint

`GET /api/v1/catalog/products/compare?id={uuid}&id={uuid}`

- Anonymous, read-only, không CSRF.
- `id`: bắt buộc 1–4 UUID duy nhất.
- Response dùng `Cache-Control: no-store` vì regular price và availability có thể thay đổi.
- Frontend route vẫn dùng dạng dễ chia sẻ `/compare?ids=id1,id2`; loader chuyển thành repeated `id` khi gọi API.

### Response đề xuất

```json
{
  "items": [
    {
      "id": "...",
      "title": "Vợt cầu lông ...",
      "description": "...",
      "category": { "id": "...", "name": "Vợt cầu lông", "slug": "vot-cau-long" },
      "brand": { "id": "...", "name": "Yonex", "slug": "yonex", "country": "Japan", "websiteUrl": null },
      "skus": [],
      "images": [],
      "specifications": [],
      "averageRating": 4.8,
      "reviewCount": 23
    }
  ],
  "requestedCount": 2,
  "omittedCount": 0
}
```

`items` tái sử dụng projection `ProductDetailDto` để tránh contract lệch giữa PDP và comparison.

### Error contract

- `400 VALIDATION_ERROR`: thiếu ID, quá bốn ID, UUID không hợp lệ hoặc trùng ID ở API boundary.
- `400 COMPARISON_CATEGORY_MISMATCH`: các Product public còn lại không cùng danh mục.
- `429`: policy public Catalog nếu vượt rate limit được cấu hình.
- `500`: không được dùng cho lỗi input, lifecycle omission hoặc category mismatch.

Không trả `404` theo từng ID trong batch; `omittedCount` là thông báo generic để link cũ vẫn mở được.

## 5. Backend design

### Application

Mở rộng `CatalogContracts.cs`:

- `ProductComparisonQuery : IValidatableObject` với `IReadOnlyList<Guid> Id` hoặc model binder tương đương.
- `ProductComparisonDto(IReadOnlyList<ProductDetailDto> Items, int RequestedCount, int OmittedCount)`.
- `ICatalogReader.GetComparisonAsync(IReadOnlyList<Guid> ids, CancellationToken)`.
- Lỗi domain/application riêng cho category mismatch hoặc result union có discriminated failure; không throw generic exception.

### Infrastructure

`PostgresCatalogReader.GetComparisonAsync` phải:

1. Deduplicate/validation đã hoàn thành trước khi query.
2. Query tập ID bằng `Contains` trên cùng `PublicProducts` predicate hiện có.
3. Project category/brand/active SKU/media/spec/review giống `GetProductAsync`.
4. Không gọi `GetProductAsync` tuần tự bốn lần và không lazy-load navigation.
5. Sau materialization tối đa bốn item, kiểm tra underlying `CategoryId` cùng nhau và sắp lại theo request order.
6. Omit item không public; không log lifecycle/title/ID ở mức warning cho request public bình thường.
7. Tôn trọng cancellation/request timeout của Catalog.

Nên trích projection dùng chung giữa `GetProductAsync` và comparison nếu EF Core vẫn dịch được; nếu expression làm SQL quá phức tạp, giữ hai projection tường minh nhưng có contract tests chống drift.

### API

`CatalogController.Compare`:

- Route đặt tại `products/compare`; route `{id:guid}` hiện có không xung đột nhờ constraint.
- Validate 1–4 và unique trước service.
- `Cache-Control: no-store`.
- Map mismatch thành RFC 7807 với `code = COMPARISON_CATEGORY_MISMATCH` và `correlationId` theo convention dự án.

### Database/demo data

- Không migration.
- Mở rộng `007_demo_seed.sql` bằng `INSERT ... ON CONFLICT (product_id, spec_key) DO UPDATE/NOTHING` cho hai Product vợt `b000...001` và `b000...002`.
- Dùng chung key thực tế như `weight_class`, `balance`, `shaft_stiffness`, `max_tension`, `material`; khác value để Flow 18 chứng minh highlight.
- Seed vẫn idempotent và không ghi đè dữ liệu vận hành ngoài đúng demo IDs.

## 6. Frontend state và data flow

### Storage contract

Tạo feature `frontend/src/features/comparison/`:

- Key: `hcc.product-comparison.v1`.
- Stored item tối thiểu: `id`, `title`, `imageUrl`, `categoryId`, `categoryName`.
- Parser runtime bắt buộc kiểm tra object/array/string, UUID, deduplicate và cap bốn; JSON hỏng phải reset an toàn, không làm crash AppShell.
- Storage là account-agnostic, không bị xóa/merge khi login/logout.
- `storage` event đồng bộ tab khác; custom event `hcc:comparison-changed` đồng bộ trong cùng tab.

### ComparisonProvider

- Bọc public `AppShell` tương tự `WishlistProvider`, nhưng không gọi API nền.
- API context: `items`, `count`, `contains`, `add`, `remove`, `clear`, `canCompareWith`.
- Add idempotent; double click không nhân bản.
- Mục thứ năm trả kết quả rõ để UI thông báo “Tối đa 4 sản phẩm”.
- Category mismatch được chặn sớm khi cả hai phía có public `categoryId`; category null được phép thêm tạm và backend xác nhận khi mở trang.

### URL và loader

- `comparisonLoader` đọc `ids` comma-separated, trim, UUID validate, deduplicate giữ first occurrence, cap bốn.
- URL không hợp lệ toàn phần trả Catalog-style 400; URL có duplicate được canonicalize bằng replace navigation.
- Loader gọi một batch API; runtime parser kiểm tra response và không tin dữ liệu mạng.
- Sau load, trang đồng bộ provider/localStorage bằng item public thực tế nhưng không tự điều hướng làm mất URL gốc trước khi hiện notice unavailable.

## 7. UI/UX

### ProductCard và PDP

- Reusable `CompareButton` có `aria-pressed`, label chứa tên Product và pending-independent vì chỉ mutation local.
- ProductCard đặt control không che Wishlist, không nằm trong link và chặn event bubble.
- PDP đặt cạnh Wishlist/alert action; trạng thái “Đã chọn so sánh” rõ bằng icon + text, không chỉ bằng màu.
- Khi mismatch hoặc đủ bốn mục: live-region message và CTA mở/clear comparison; không silently thay thế lựa chọn cũ.

### Comparison tray

- Chỉ hiện khi có ít nhất một item và không hiện trong `/admin`, `/staff`, auth pages.
- Hiển thị tối đa bốn thumbnail/title rút gọn, remove từng item, “Xóa tất cả”.
- CTA “So sánh ngay (n)” disabled khi `n < 2`; vẫn có link thêm sản phẩm về `/#catalog`.
- Mobile dùng bottom sheet compact, không che CTA hệ thống hoặc nội dung cuối trang; tôn trọng safe-area.

### ComparePage `/compare`

- Breadcrumb, heading và mô tả số Product.
- Empty state cho 0 item; prompt cho 1 item; matrix cho 2–4 item.
- Header Product có ảnh, brand/title, min price, stock, rating, remove và link PDP.
- Dòng core: Thương hiệu, Giá từ, Tình trạng, Đánh giá, Các phiên bản SKU.
- Dòng spec union theo key; nhóm “Thông số kỹ thuật”.
- Toggle “Chỉ hiển thị điểm khác nhau” là state URL/local của trang, không thay đổi danh sách IDs.
- Notice generic khi `omittedCount > 0`; provider xóa mục stale sau khi user xác nhận hoặc sau load thành công theo quyết định implementation, không flash dữ liệu cũ.
- Table semantic có `<caption>`, `scope="col"`, `scope="row"`; mobile wrapper có label hướng dẫn cuộn ngang.

## 8. Accessibility và responsive gate

- Tất cả compare controls keyboard-accessible, focus ring rõ, hit target tối thiểu 44px trên mobile.
- Tray open/close không cướp focus; sau remove, focus sang item kế hoặc CTA phù hợp.
- Không dùng màu làm tín hiệu duy nhất cho different row; thêm text/icon ẩn/hiện phù hợp.
- `prefers-reduced-motion` tắt transition tray.
- 320px: không overflow body; chỉ vùng matrix cuộn ngang.
- 768px: tối thiểu hai Product columns đọc được.
- Desktop: sticky first column và product headers nhưng không che SiteHeader.
- Empty/error/loading states dùng `role=status`/`role=alert` đúng mức, không spam announcement.

## 9. Security, privacy và performance

- Chỉ projection public; không trả creator, internal lifecycle, cost, tồn chi tiết hoặc revision nội bộ.
- Image URL dùng cùng normalization hiện tại; không render HTML từ description/spec.
- Không đặt dữ liệu nhạy cảm trong localStorage; chỉ metadata Catalog public.
- UUID encode bằng `URLSearchParams`; không nối raw query string.
- API cap bốn trước DB; query parameter length hữu hạn; cancellation propagates tới EF.
- Integration test command counting chứng minh bounded query, không N+1 theo số Product/spec/SKU.
- Không cache giá/tồn compare ở service worker/CDN trong T52.
- Không analytics hoặc fingerprint từ comparison IDs.

## 10. Test matrix bắt buộc

### Backend integration

1. Anonymous đọc comparison thành công, thứ tự response đúng request.
2. Một đến bốn ID hợp lệ; zero, duplicate, malformed và năm ID trả 400, không 500.
3. Draft/SUSPENDED/ARCHIVED/không active SKU bị omit và tăng `omittedCount`.
4. Hai Product public khác category trả `COMPARISON_CATEGORY_MISMATCH`.
5. Hidden category không lộ metadata nhưng backend vẫn kiểm tra underlying category chính xác.
6. Price/stock/SKU/spec/brand/rating projection khớp Catalog/PDP.
7. Không lộ inventory partitions hoặc internal fields trong JSON.
8. Request order ổn định bất kể DB order.
9. SQL command count bounded; dataset nhiều spec/SKU không tạo N+1.
10. Cancellation/invalid lifecycle không biến thành 500.
11. Demo seed chạy hai lần không nhân bản spec.

### Frontend unit/component

12. Storage parser xử lý JSON hỏng, duplicate, hơn bốn item và storage unavailable.
13. Add/remove/clear idempotent; cross-tab/custom event cập nhật state.
14. Chặn category mismatch và item thứ năm với message accessible.
15. ProductCard/PDP phản ánh `aria-pressed` và không kích hoạt link cha.
16. Tray count/CTA/focus/clear đúng ở desktop/mobile.
17. Loader canonicalize IDs, gọi đúng một batch request và parser từ chối malformed response.
18. ComparePage order, unavailable notice, core rows, spec union/missing value và different-only toggle đúng.
19. URL reload/back-forward/share giữ danh sách; URL là source of truth thay vì localStorage cũ.
20. Semantic table, loading/error/empty/one-item states và 320px overflow gate.

### Playwright Flow 18

Trên Docker demo stack thật:

1. Guest vào Catalog, thêm hai vợt demo cùng category từ hai card/PDP.
2. Tray hiển thị 2/4, reload vẫn giữ lựa chọn.
3. Mở `/compare`; URL chứa đúng hai ID theo thứ tự.
4. Matrix hiển thị title, regular price, availability, SKU và các spec có khác biệt.
5. Copy URL sang browser context mới vẫn tải được comparison dù localStorage trống.
6. Bật “chỉ điểm khác nhau”, xóa một Product, thêm lại và không duplicate.
7. Thử thêm giày khác category bị chặn rõ, không phá hai lựa chọn vợt.
8. Suspend một Product bằng Admin; reload compare hiển thị generic unavailable và không lộ trạng thái `SUSPENDED`.

Regression tối thiểu: Flow 5 Wishlist, Flow 12 lifecycle, Flow 16 search và Flow 17 alerts; full backend/frontend/lint/build/Compose.

## 11. Chia task triển khai

| Task | Công việc | Điều kiện hoàn tất |
|---|---|---|
| T52.1 | Chốt contract, API docs skeleton và Catalog comparison DTO/query | **Hoàn tất 08/11/2026** — `ProductComparisonQuery` cap 1–4 unique/non-empty UUID, response DTO giữ order/count, error code category mismatch và 4 contract tests |
| T52.2 | Batch reader + Controller + projection hardening | **Hoàn tất 08/11/2026** — anonymous `GET /catalog/products/compare`, public predicate/projection, request order, generic omission, underlying-category guard kể cả taxonomy ẩn, `no-store`, RFC 7807 mismatch và đúng một DB command; 14/14 T52 tests, full backend 455/455 |
| T52.3 | Demo seed specifications | **Hoàn tất 08/11/2026** — 6 key chuẩn hóa/12 dòng cho hai vợt demo, có giá trị chung và khác biệt; scoped upsert chỉ ghi khi đổi, reseed không duplicate; API trả đủ matrix data; 15/15 seed + comparison tests |
| T52.4 | Comparison storage/context/button/tray | **Hoàn tất 08/11/2026** — storage parser UUID/dedupe/cap 4 và corrupt/private-mode recovery; account-agnostic Context đồng bộ storage/custom event, category/limit guards; accessible CompareButton tại Catalog/PDP và responsive tray remove/clear/live notice. CTA chưa điều hướng trước T52.5; 5/5 feature tests, full frontend 156/156, lint/build sạch |
| T52.5 | `/compare` loader/page/matrix/responsive UX | **Hoàn tất 08/11/2026** — URL-source-of-truth loader canonicalize/dedupe/cap, strict batch parser/order/count, storage reconciliation, empty/one/unavailable states, semantic horizontally-scrollable matrix, core + union spec rows, missing value, different-only URL state, remove navigation và CTA tray được kích hoạt; 9/9 feature tests, full frontend 160/160, lint/build sạch |
| T52.6 | Hardening/security/performance/regression | **Hoàn tất 08/11/2026** — batch query timeout 2 giây có restore; defensive unique/non-empty guard; strict client rating/count/order/category parser; localStorage image URL sanitization; .NET/PostgreSQL GUID-format parser loại Empty nhưng không ép RFC version nibble; remove giữ `different=true` và chuyển keyboard focus; category-mismatch UX chuyên biệt. xUnit runner giới hạn 4 collection threads/conservative để release gate Testcontainers tái lập. Kết quả: backend 455/455, frontend 162/162, T52 frontend 11/11, lint/build và base+demo Compose sạch |
| T52.7 | Playwright Flow 18 + release docs/gate/Graphify | **Hoàn tất 08/11/2026** — Flow 18 pass Docker/PostgreSQL thật: Guest select/persist/share URL, matrix/different-only, remove-add không duplicate, khác category bị chặn và suspend bị omit generic rồi resume trong `finally`. Regression Flows 5/12/16/17 đạt 4/4. CI discover `18-product-comparison.spec.ts` và chạy Flows 1–18; docs release/E2E/API/README/TASKS đồng bộ. Playwright 20 tests/18 specs; Graphify cập nhật sau source cuối |

Thứ tự: T52.1 -> T52.2 -> T52.3 -> T52.4 -> T52.5 -> T52.6 -> T52.7.

Không mở ProductCard/PDP controls trước khi batch API visibility/category tests của T52.2 xanh.

## 12. Files dự kiến

- `HighConcurrencyCommerce.Application/Catalog/CatalogContracts.cs`
- `HighConcurrencyCommerce.Infrastructure/Catalog/PostgresCatalogReader.cs`
- `HighConcurrencyCommerce.Api/Controllers/CatalogController.cs`
- `HighConcurrencyCommerce.IntegrationTests/CatalogTests.cs` hoặc `ProductComparisonTests.cs`
- `database/007_demo_seed.sql`
- `frontend/src/features/comparison/comparison.api.ts`
- `frontend/src/features/comparison/comparison-storage.ts`
- `frontend/src/features/comparison/ComparisonContext.tsx`, context/hook
- `frontend/src/features/comparison/CompareButton.tsx`
- `frontend/src/features/comparison/ComparisonTray.tsx`
- `frontend/src/pages/ComparePage.tsx`
- `frontend/src/App.tsx`, `AppShell.tsx`, `ProductCard.tsx`, `ProductDetailPage.tsx`
- CSS feature/page responsive styles
- `frontend/src/test/product-comparison.test.tsx`
- `frontend/e2e/18-product-comparison.spec.ts`
- `docs/api/product-comparison.md`
- E2E/release docs, `README.md`, `docs/TASKS.md`, CI discovery gate

## 13. Release gate

Chỉ đóng T52 khi:

- Batch API giữ request order, cap bốn, không N+1 và không lộ Product/lifecycle không public.
- Mixed category bị chặn ở backend; frontend guard chỉ là UX bổ sung.
- URL share/reload/back-forward hoạt động độc lập localStorage.
- Storage hỏng/quota/private mode không làm crash Storefront.
- Matrix dùng regular price và availability mới từ API, không dùng snapshot tray.
- Accessibility/320px/mobile keyboard gates đạt.
- Flow 18 và regression Flow 5/12/16/17 pass trên Docker thật.
- Full backend/frontend/lint/build/base+demo Compose pass.
- `graphify update .` chạy sau code cuối và tài liệu/evidence cập nhật theo số thực tế.

## 14. Rủi ro và điểm dừng

- Nếu EF projection collection cho batch sinh cartesian explosion hoặc quá nhiều command, đo SQL rồi dùng split query có số command cố định; không chấp nhận N+1 theo Product.
- Nếu taxonomy hiện tại có Product public không category, UI cho phép chọn nhưng API chỉ compare khi underlying category đồng nhất; không tự gom vào category giả.
- Nếu spec key cùng nghĩa nhưng khác tên (`weight`/`weight_class`), giữ tách riêng trong T52; chuẩn hóa taxonomy spec phải là task dữ liệu độc lập.
- Nếu sticky table gây lỗi screen reader/mobile, ưu tiên semantic table và horizontal scroll ổn định hơn hiệu ứng sticky.
- Nếu product lifecycle đổi giữa request và render, response là snapshot read; reload lấy trạng thái mới. T52 không thêm transaction lock vào public read.
