# Public Catalog Implementation Plan

**Goal:** Khách xem danh mục, tìm/lọc/phân trang sản phẩm và xem SKU từ PostgreSQL qua API C# và React.

**Architecture:** Controller mỏng → `ICatalogReader` ở Application → EF/Npgsql reader ở Infrastructure. React Router loaders đọc dữ liệu, hủy request cũ bằng signal và render DTO đã validate. Giữ FE/BE tách riêng.

**Tech Stack:** .NET 10, EF/Npgsql 10, PostgreSQL local, React 19, TypeScript, React Router, Vitest/Testing Library/MSW.

**Spec:** `../../../README.md`, FR-M02-001; task T01–T06 trong `../../TASKS.md`.

**Execution:** Triển khai trực tiếp trong phiên theo yêu cầu “bắt tay vào code”; tự review và chạy test. Không cần thêm bước xác nhận kế hoạch. Skill workflow được áp dụng trong phạm vi task đã được giao.

## Global constraints

- PostgreSQL là source of truth; catalog chỉ đọc, không giữ hàng.
- Public Product chỉ PUBLISHED và có ít nhất một ACTIVE SKU. Hết hàng vẫn được xem.
- Chỉ category visible xuất hiện trong danh sách category; category ẩn không tự unpublish Product (chưa có rule đó). DTO của Product không lộ tên/categoryId ẩn; lọc category ẩn trả rỗng.
- Giá regular và stock là dữ liệu tham khảo tại thời điểm đọc; checkout phải xác minh lại. Chưa áp giá Flash Sale ở đợt này.
- Test tạo DB tên `hcc_tests_<guid>` trên PostgreSQL loopback bằng credentials local/env; không thay database ứng dụng. Schema/seed không tự chạy khi API khởi động.
- Admin/Staff/auth/cart/payments vẫn chưa triển khai; UI không đưa link demo nội bộ ra như chức năng thật.

## Review focus

1. Draft/Archived và INACTIVE SKU bị lộ → test list/detail visibility.
2. Hết hàng hoặc không có InventoryBalance → còn hiển thị, `inStock=false`, không trả lượng reserved/allocated.
3. Search `%`, `_`, quote → literal, parameterized; query sai trang/giá/id trả 400.
4. Page rỗng, stable ordering và lọc giá nhiều SKU → test cùng SKU đáp ứng khoảng giá; minPrice list vẫn là giá thấp nhất của ACTIVE SKU.
5. HTTP/network/schema lỗi và response cũ → FE có error/retry/loading, request.signal; không fallback sang demo như dữ liệu thật.

## T01–T04: API và PostgreSQL test

**Files:** `database/001_create_schema.sql`; `HighConcurrencyCommerce/HighConcurrencyCommerce.IntegrationTests/*`; Application `Catalog/*`; Infrastructure `Catalog/PostgresCatalogReader.cs`; API `Controllers/CatalogController.cs`, `Errors/*`, `Program.cs`.

**Interfaces:** `ICatalogReader.GetCategoriesAsync(CancellationToken)`, `GetProductsAsync(CatalogQuery,CancellationToken)`, `GetProductAsync(Guid,CancellationToken)`; DTO categories, paged product summaries, detail with ACTIVE SKUs.

- [x] Viết HTTP tests: public visibility, validation, search/filter/sort/page, detail/not-found, no private fields.
- [x] Chạy tests với fixture PostgreSQL; thấy 404 vì route chưa có (RED).
- [x] Implement DTO/query validation, projected read-only EF queries, controllers và ProblemDetails.
- [x] Chạy toàn bộ BE tests, sửa lỗi và review SQL-generated queries.

## T05–T06: React và bàn giao

**Files:** `frontend/src/lib/api/client.ts`, `features/catalog/*`, `pages/StorefrontPage.tsx`, `pages/ProductDetailPage.tsx`, route composition; test setup và documentation.

**Interfaces:** GET `/api/v1/catalog/categories`, `/products?q=&categoryId=&minPrice=&maxPrice=&sort=&page=&pageSize=`, `/products/{id}`. Exact DTO documented ở `docs/api/catalog.md`.

- [x] Viết tests client + catalog screens với mock ở HTTP boundary.
- [x] Xác nhận RED vì UI đang dùng demo hoặc thiếu route (6 screen tests fail trước implementation).
- [x] Implement loaders, runtime guards, tìm/lọc/phân trang và chi tiết; bỏ demo từ storefront.
- [x] Chạy frontend tests/build/lint và backend tests; ghi evidence và giới hạn.
- [x] Cập nhật README, API contract, TASKS để phiên sau biết điểm tiếp tục.

## Kết quả 30/10/2026

- `dotnet test HighConcurrencyCommerce.slnx`: 25 passed, 0 failed.
- `dotnet build HighConcurrencyCommerce.slnx --no-restore`: 0 warnings/errors.
- `npm test`: 12 passed; `npm run build`, `npm run lint`: exit 0.
- Smoke HTTP: frontend, API products, proxy products/categories đều 200. Dùng Vite port 5183 trong smoke vì 5173 đã có tiến trình khác; cấu hình mặc định vẫn 5173 → 5291.
- Database ứng dụng trả 0 products, không sửa/seed DB đó. Fixture riêng chứng minh list/detail trên dữ liệu Published/Draft/Archived và các trạng thái stock.
- Đã giải quyết lệch EF runtime transitive bằng reference EF.Relational 10.0.14 trong Infrastructure; test graph dùng cùng runtime với API. MSW 3 dùng `onUnhandledFrame` theo type/source package cài đặt.
- Chưa có media URL nên ảnh chuyển T12; chưa có giá Flash Sale, auth, cart/checkout. Tests FE ở DOM/HTTP boundary, chưa có browser E2E hoặc đánh giá hiển thị bằng screenshot.

## Tài liệu kỹ thuật tham khảo

- https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0
- https://www.npgsql.org/efcore/mapping/translations.html
- https://reactrouter.com/start/data/custom
