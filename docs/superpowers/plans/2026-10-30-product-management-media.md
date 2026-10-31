# Product Management and Media (T12) Implementation Plan

**Goal:** Triển khai quản trị sản phẩm với sự phân tách quyền nghiêm ngặt giữa Staff (soạn Draft, thêm biến thể SKU, quản lý ảnh) và Admin (định giá SKU, Publish sản phẩm), ghi nhận Audit Log cho các thay đổi nhạy cảm, và kết nối hiển thị ảnh thật (thay thế placeholder) trên Public Catalog.

**Architecture:** Controller mỏng (`StaffProductController`, `AdminProductController`) → `IProductManagementService` ở Application → `PostgresProductManagementService` ở Infrastructure ghi dữ liệu vào schema `store` và `store.audit_logs`. Frontend cập nhật `ProductCard` & `ProductDetailPage` hiển thị ảnh thật, cùng màn hình vận hành tại `StaffDashboardPage` & `AdminDashboardPage`.

**Tech Stack:** .NET 10, EF/Npgsql 10, PostgreSQL local, React 19, TypeScript, React Router, Vitest.

**Spec:** `README.md` (M02 Product & Catalog, E04, E05, E06, E07, E31, Sec 5, Sec 7); task T12 trong `docs/TASKS.md`.

---

## 1. Global Constraints & Business Invariants

- **Role Separation:**
  - Staff: Chỉ được tạo/sửa sản phẩm ở trạng thái `DRAFT`, thêm SKU, thêm/sửa/xóa ảnh sản phẩm. Staff không được đổi giá SKU và không được Publish sản phẩm.
  - Admin: Có thẩm quyền đặt/cập nhật giá SKU và Publish sản phẩm.
- **Publish Invariants:**
  - Sản phẩm phải ở trạng thái `DRAFT`.
  - Phải có ít nhất 1 SKU `ACTIVE`.
  - Tất cả các SKU `ACTIVE` phải có giá `regular_price > 0`.
- **Audit Invariants:**
  - Mọi thao tác cập nhật giá SKU và Publish sản phẩm phải được ghi vào `store.audit_logs` với `actor_id`, `actor_type = 'USER'`, `correlation_id`, `before_data`, `after_data`.
- **Media & Catalog Invariants:**
  - Bảng `store.product_images` lưu trữ `file_key`, `alt_text`, `display_order`, `image_status`.
  - Catalog Public chỉ trả về các ảnh `ACTIVE`, sắp xếp theo `display_order ASC, created_at ASC`.
  - `ProductSummaryDto` trả về `imageUrl` là ảnh active đầu tiên để hiển thị trên danh sách sản phẩm.
  - React storefront và product detail thay thế placeholder bằng ảnh thật nếu có.

---

## 2. API Contract

Chi tiết tại `docs/api/product-management.md`.

### Staff Endpoints:
- `POST /api/v1/staff/products`
- `PATCH /api/v1/staff/products/{id}/draft`
- `POST /api/v1/staff/products/{id}/skus`
- `POST /api/v1/staff/products/{id}/images`
- `DELETE /api/v1/staff/products/{id}/images/{imageId}`

### Admin Endpoints:
- `PATCH /api/v1/admin/skus/{id}/price`
- `POST /api/v1/admin/products/{id}/publish`

---

## 3. Implementation Steps

1. Mở rộng DTOs trong `HighConcurrencyCommerce.Application/Catalog/CatalogContracts.cs` để hỗ trợ ảnh.
2. Tạo contracts trong `HighConcurrencyCommerce.Application/ProductManagement/ProductManagementContracts.cs`.
3. Cập nhật `PostgresCatalogReader.cs` trong Infrastructure để truy vấn ảnh active.
4. Triển khai `PostgresProductManagementService.cs` trong Infrastructure kèm transactional audit logs.
5. Tạo `StaffProductController.cs` và `AdminProductController.cs` trong API layer, đăng ký DI và CSRF.
6. Viết bộ integration test `ProductManagementTests.cs` (phân quyền, draft CRUD, audit log, publish invariants, public catalog reflection).
7. Cập nhật frontend: `catalog.api.ts`, `ProductCard.tsx`, `ProductDetailPage.tsx`, `StaffDashboardPage.tsx`, `AdminDashboardPage.tsx`.
8. Chạy kiểm thử toàn diện BE/FE, lint, build và cập nhật Graphify.

