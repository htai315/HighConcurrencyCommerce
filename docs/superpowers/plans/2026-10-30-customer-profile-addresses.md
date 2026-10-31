# Customer Profile and Addresses (T11) Implementation Plan

**Goal:** Khách hàng (Customer) xem/cập nhật thông tin hồ sơ của mình và quản lý sổ địa chỉ giao hàng (`customer_addresses`) với cơ chế owner-only và bảo vệ 1 địa chỉ mặc định duy nhất dưới điều kiện đồng thời cao.

**Architecture:** Controller mỏng (`AccountController`) → `IAccountService` ở Application → `PostgresAccountService` ở Infrastructure. Toàn bộ logic giao dịch và bảo vệ concurrency nằm trong transaction DB. Frontend React Router hiển thị hồ sơ và quản lý sổ địa chỉ.

**Tech Stack:** .NET 10, EF/Npgsql 10, PostgreSQL local, React 19, TypeScript, React Router, Vitest.

**Spec:** `README.md` (M01 Account & Access, E01, E03); task T11 trong `docs/TASKS.md`.

---

## 1. Global Constraints & Business Invariants

- **Role:** Chỉ `CUSTOMER` đã đăng nhập phiên hợp lệ mới có quyền truy cập (`[Authorize(Policy = "Customer")]`). Guest trả 401; Staff/Admin trả 403.
- **Owner-only:** Customer ID được lấy trực tiếp từ session (`ClaimTypes.NameIdentifier`), không tin cậy bất kỳ ID nào do client gửi. Mọi truy vấn/cập nhật/xóa địa chỉ đều phải có điều kiện `customer_id = currentUserId`. Nếu không tìm thấy, trả về `404 Not Found`.
- **Default Address Concurrency:**
  - Bảng `customer_addresses` có partial unique index `ux_default_address_per_customer (customer_id) WHERE is_default;`.
  - Khi tạo hoặc sửa một địa chỉ thành `isDefault = true`, transaction phải unset default cũ trước khi gán default mới.
  - Race-condition giữa 2 request đồng thời đều muốn làm default: 1 request thắng commit, request kia được xử lý an toàn không vi phạm DB constraint.
  - Nếu customer tạo địa chỉ đầu tiên: tự động gán `isDefault = true` nếu client không chỉ định.
- **Ràng buộc khóa ngoại khi xóa:**
  - Nếu địa chỉ đã được gắn vào `sales_orders.shipping_address_id`, xóa vật lý sẽ vi phạm FK. Bắt lỗi này và trả về `409 Conflict` với mã lỗi `ADDRESS_IN_USE`.

---

## 2. API Contract

Chi tiết tại `docs/api/account.md`.
- `GET /api/v1/account/profile`
- `PATCH /api/v1/account/profile`
- `GET /api/v1/account/addresses`
- `POST /api/v1/account/addresses`
- `PATCH /api/v1/account/addresses/{id}`
- `DELETE /api/v1/account/addresses/{id}`

---

## 3. Implementation Steps

1. Sửa `UserAccount.cs` và `StoreDbContext.cs`: chuyển `CustomerAddress? CustomerAddress` thành 1-N `ICollection<CustomerAddress> CustomerAddresses`.
2. Tạo contracts trong `HighConcurrencyCommerce.Application/Account/AccountContracts.cs`.
3. Triển khai `PostgresAccountService.cs` trong `HighConcurrencyCommerce.Infrastructure/Account/`.
4. Tạo `AccountController.cs` trong `HighConcurrencyCommerce.Api/Controllers/` và đăng ký service trong `Program.cs`.
5. Viết integration tests `AccountTests.cs` (Profile, Address CRUD, Owner Isolation, Concurrency Default, In-use Delete).
6. Triển khai React API client và UI trong `frontend/`.
7. Chạy test FE/BE, lint, build.
8. Cập nhật `TASKS.md`, `README.md`, chạy `python -m graphify update .`.

