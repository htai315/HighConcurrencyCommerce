# Kế hoạch triển khai Task T13: Kho — Xem tồn, Nhập tăng và Lịch sử biến động

> **Mục tiêu:** Cung cấp đầy đủ tính năng quản lý kho cơ sở cho Staff: truy vấn tồn kho theo SKU, xem lịch sử movement (sổ cái biến động kho) và nhập tăng tồn kho trực tiếp với bảo đảm tính toán vẹn bất biến (`physical_qty = available_qty + reserved_qty + allocated_qty`), khóa hàng `FOR UPDATE`, hỗ trợ Idempotency và kiểm thử đồng thời (concurrency tests).

---

## 1. Business Invariants & Quy tắc nghiệp vụ

1. **Đẳng thức phân vùng tồn kho:**
   $$\text{PhysicalQty} = \text{AvailableQty} + \text{ReservedQty} + \text{AllocatedQty}$$
   Tất cả các thành phần phải $\ge 0$.
2. **Nhập tăng tồn kho (Stock-in):**
   - Do **Staff** trực tiếp thực hiện.
   - Số lượng nhập $X$ phải là số nguyên dương $> 0$.
   - Khi nhập tăng:
     - $\text{PhysicalQty} \leftarrow \text{PhysicalQty} + X$
     - $\text{AvailableQty} \leftarrow \text{AvailableQty} + X$
     - $\text{ReservedQty}$ và $\text{AllocatedQty}$ không đổi.
     - $\text{Revision} \leftarrow \text{Revision} + 1$.
   - Đẳng thức phân vùng tồn kho luôn được bảo toàn sau khi nhập.
3. **Sổ cái dịch chuyển (Inventory Movement Ledger):**
   - Mọi lần nhập tăng phải ghi một bản ghi vào `store.inventory_movements`:
     - `movement_type = 'STOCK_IN'`
     - `qty_delta = X` ($> 0$)
     - `source_type` (mặc định `'MANUAL_STOCK_IN'` hoặc client chỉ định ví dụ `'PURCHASE_RECEIPT'`)
     - `source_id` (Guid định danh nguồn / mã phiếu nhập)
     - `actor_id` (Guid của Staff thực hiện)
     - `note` (Ghi chú lý do nhập)
4. **Idempotency & Concurrency:**
   - Database có ràng buộc duy nhất: `UNIQUE (product_sku_id, movement_type, source_type, source_id)`.
   - Nếu client gửi lại cùng `(productSkuId, 'STOCK_IN', sourceType, sourceId)`, hệ thống phát hiện đã thực hiện và trả về kết quả tồn kho hiện tại (idempotent replay) thay vì cộng dồn số lượng lần thứ hai.
   - Khóa hàng `store.inventory_balances WHERE product_sku_id = {productSkuId} FOR UPDATE` bảo đảm tuần tự hóa các thao tác cập nhật tồn kho song song, không gây thất thoát số lượng (lost update).
5. **Phân quyền (RBAC):**
   - `Guest` $\rightarrow$ 401 Unauthorized.
   - `Customer` $\rightarrow$ 403 Forbidden.
   - `Staff` $\rightarrow$ Đầy đủ quyền xem tồn, xem lịch sử và nhập tăng kho.
   - `Admin` $\rightarrow$ Có thể xem tồn và lịch sử kho.

---

## 2. Thiết kế API Endpoints

1. **`GET /api/v1/staff/inventory`**
   - Quyền: Staff
   - Response: `200 OK` danh sách `SkuInventorySummaryDto[]`
2. **`GET /api/v1/staff/inventory/{skuId}/movements`**
   - Quyền: Staff
   - Response: `200 OK` danh sách `InventoryMovementDto[]` sắp xếp theo `created_at DESC`
3. **`POST /api/v1/staff/inventory/stock-in`**
   - Quyền: Staff
   - Request body: `StockInRequest { productSkuId, quantity, sourceType?, sourceId?, note? }`
   - Response: `200 OK` (hoặc `201 Created`) `StockInResultDto`

---

## 3. Các bước thực hiện

### Bước 1: Contracts & Exception Handlers (Application Layer)
- Tạo `HighConcurrencyCommerce.Application/Inventory/InventoryContracts.cs`:
  - `SkuInventorySummaryDto`: `SkuId`, `SkuCode`, `OptionLabel`, `ProductId`, `ProductTitle`, `PhysicalQty`, `AvailableQty`, `ReservedQty`, `AllocatedQty`, `SkuStatus`, `PublicationStatus`.
  - `InventoryMovementDto`: `Id`, `ProductSkuId`, `MovementType`, `QtyDelta`, `SourceType`, `SourceId`, `ActorId`, `ActorName`, `Note`, `CreatedAt`.
  - `StockInRequest`: Data annotations validation (`Quantity > 0`, `ProductSkuId != empty`).
  - `StockInResultDto`: `BalanceId`, `ProductSkuId`, `PhysicalQty`, `AvailableQty`, `ReservedQty`, `AllocatedQty`, `Revision`, `MovementId`.
  - `InventoryFailure`: Exception ném mã lỗi (`NOT_FOUND`, `INVALID_ARGUMENT`, `CONFLICT`).
  - `IInventoryService`: Định nghĩa interface các phương thức quản lý tồn kho.
- Tạo `HighConcurrencyCommerce.Api/ExceptionHandlers/InventoryExceptionHandler.cs`: Ánh xạ `InventoryFailure` sang RFC 7807 problem details (404, 400, 409).

### Bước 2: Implementation (Infrastructure Layer)
- Tạo `HighConcurrencyCommerce.Infrastructure/Inventory/PostgresInventoryService.cs`:
  - `GetInventorySummariesAsync`: Query projection từ `ProductSku` + `InventoryBalance` + `Product`.
  - `GetMovementsBySkuAsync`: Query `InventoryMovements` kèm include Actor, order by `CreatedAt DESC`.
  - `StockInAsync`: Mở transaction, kiểm tra idempotent replay, khóa `store.inventory_balances ... FOR UPDATE`, cộng dồn số lượng, ghi `inventory_movements`, bắt `DbUpdateException` (unique violation) chuyển thành `CONFLICT`.

### Bước 3: Controller & Dependency Injection (Api Layer)
- Tạo `HighConcurrencyCommerce.Api/Controllers/StaffInventoryController.cs`:
  - Gán `[Authorize(Policy = "Staff")]`, `[Route("api/v1/staff/inventory")]`.
- Đăng ký service và exception handler trong `Program.cs`.

### Bước 4: Backend Integration Tests
- Tạo `HighConcurrencyCommerce.IntegrationTests/InventoryTests.cs`:
  - Test RBAC: Guest 401, Customer 403, Staff 200.
  - Test Stock-In happy path: kiểm tra tăng `physical_qty` và `available_qty`, giữ nguyên `reserved_qty` và `allocated_qty`, kiểm tra ghi đúng `inventory_movements`.
  - Test Stock-In validation: quantity <= 0, SKU không tồn tại.
  - Test Idempotency: Gửi lại cùng `sourceId` và `sourceType` $\rightarrow$ không bị cộng dồn 2 lần.
  - Test Concurrency (Race conditions):
    - Chạy 10 request nhập kho song song cho cùng 1 SKU $\rightarrow$ `physical_qty` và `available_qty` tăng chính xác bằng tổng số lượng (không bị lost update).
    - Chạy 2 request nhập kho song song với cùng `sourceId` $\rightarrow$ 1 request thực thi, request kia nhận kết quả idempotent an toàn, không sinh lỗi 500.

### Bước 5: Frontend UI & API Client
- Thêm `frontend/src/features/inventory/inventory.api.ts`: types, client methods (`getInventory`, `getMovements`, `stockIn`).
- Cập nhật module Vận hành kho trong `StaffDashboardPage.tsx`:
  - Tab hoặc Section "Vận hành kho & Tồn kho":
    - Danh sách tồn kho từng biến thể SKU (Mã SKU, Sản phẩm, Tồn vật lý, Khả dụng, Đang giữ, Đã phân bổ).
    - Form nhập tăng kho (chọn SKU, nhập số lượng, ghi chú).
    - Modal hoặc panel xem lịch sử biến động (Movements) của SKU.
- Viết frontend test trong `frontend/src/test/inventory.test.tsx`.

### Bước 6: Kiểm tra toàn diện & Graphify
- Chạy `dotnet test`.
- Chạy `npm test`, `npm run lint`, `npm run build`.
- Chạy `python -m graphify update .`.
- Cập nhật `docs/TASKS.md`.

