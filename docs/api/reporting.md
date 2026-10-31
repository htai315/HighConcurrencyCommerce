# Reporting, Staff Tasks & Audit API — Task T28

## Quyết định nghiệp vụ

### Doanh thu ghi nhận

Một Payment chỉ đóng góp `expectedAmount` đúng một lần khi đồng thời thỏa:

1. Order `COMPLETED`.
2. Payment `SUCCESS` hoặc `COD_COLLECTED` và có `paidAt` trong khoảng báo cáo.
3. Tồn tại PaymentTransaction `VERIFIED` có `amount == expectedAmount`.

Không cộng trực tiếp tất cả transaction, nhờ đó callback/collection trùng hoặc số tiền sai không làm phồng doanh thu. Ngày ghi nhận là UTC date của `Payment.paidAt`. Khoảng ngày gồm cả `from` và `to`, tối đa 366 ngày; mặc định 30 ngày kết thúc hôm nay UTC.

### Tồn thấp

Ngưỡng mặc định `5`, cấu hình bằng `Reporting:LowStockThreshold`. Chỉ xét SKU `ACTIVE` thuộc Product `PUBLISHED`. SKU chưa có InventoryBalance được coi là 0. Điều kiện cảnh báo là `AvailableQty <= threshold`.

### Phân quyền

- Revenue, low-stock và audit: chỉ `ADMIN`. Guest 401; Customer/Staff 403.
- Staff task summary: chỉ `STAFF`. Customer/Admin 403.
- Tất cả response có `Cache-Control: no-store` theo middleware `/admin` và `/staff`.

## `GET /api/v1/admin/reports/revenue`

Query tùy chọn: `from=YYYY-MM-DD`, `to=YYYY-MM-DD`.

Response gồm `revenue`, `completedOrderCount`, `averageOrderValue`, breakdown `days` và `paymentMethods`.

## `GET /api/v1/admin/reports/low-stock`

Trả `threshold` và danh sách SKU, gồm title/code/option cùng bốn phân vùng `physical`, `available`, `reserved`, `allocated`; sắp xếp available tăng dần rồi SKU.

## `GET /api/v1/admin/audit-logs`

Query:

- `action`: khớp chính xác.
- `entityType`: khớp chính xác.
- `actorId`: UUID.
- `from`, `to`: ngày UTC bao gồm hai đầu, tối đa 366 ngày nếu truyền cả hai.
- `page`: 1–1.000.000; `pageSize`: 1–100.

Trả actor/type, action, entity, before/after JSON, correlation ID và occurred time. Mới nhất trước. Chỉ Admin được xem vì before/after có thể chứa dữ liệu vận hành nội bộ.

## `GET /api/v1/staff/dashboard/tasks`

Snapshot thời điểm đọc:

- `pendingCodCount`
- `shipmentsToPackCount`
- `shipmentsToHandoverCount`
- `deliveryFailuresCount`
- `lowStockSkuCount`
- `notificationsUnreadCount` của chính Staff

Đây là query tổng hợp từ PostgreSQL, không phải counter trong RAM và không thay thế các API chi tiết.

## Error codes

- `400 INVALID_DATE_RANGE`: khoảng ngày rỗng hoặc vượt 366 ngày.
- `400 INVALID_ARGUMENT`: page/pageSize ngoài giới hạn.
- `400 VALIDATION_ERROR`: query không bind đúng kiểu.
- `401/403`: theo RBAC ở trên.
